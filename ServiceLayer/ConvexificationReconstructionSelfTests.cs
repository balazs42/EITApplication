using System.Diagnostics;
using System.Threading;
using BusinessLayer;
using DataAccessLayer;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction.Convexification;
using Utility.Classes.ReconstructionParameters;
using Utility.Logger;

namespace ServiceLayer
{
    /// <summary>
    /// Lightweight end-to-end checks for the convexification reconstruction path.
    /// These tests intentionally use the real service/persistence stack so startup
    /// can catch integration regressions in workspace wiring or FEM reuse.
    /// </summary>
    public static class ConvexificationReconstructionSelfTests
    {
        /// <summary>
        /// Executes all convexification-specific smoke tests and aggregates failures.
        /// </summary>
        public static void RunAll(bool throwOnFailure = false)
        {
            var failures = new List<string>();
            Try("Convexification outer iterations execute more than one cycle", TestOuterIterationExecution, failures);
            Try("Convexification boundary proxy sanity", TestBoundaryProxySanity, failures);
            Try("Convexification residual trend stabilises", TestResidualTrend, failures);
            Try("Convexification coefficient recovery has interior structure", TestCoefficientRecoverySanity, failures);
            Try("Convexification V recovery avoids trivial boundary shell", TestScaleRecoverySanity, failures);
            Try("Convexification homogeneous conductivity recovery", TestHomogeneousConductivityRecovery, failures);
            Try("Convexification interior anomaly is not boundary-only", TestInteriorAnomalyRecovery, failures);
            Try("Convexification reconstruction service integration", TestServiceIntegration, failures);

            if (failures.Count > 0)
            {
                Debug.WriteLine("Convexification self-tests failed:\n - " + string.Join("\n - ", failures));
                if (throwOnFailure)
                    throw new InvalidOperationException("Convexification self-tests failed:\n - " + string.Join("\n - ", failures));
            }
        }

        private static void Try(string name, Action test, List<string> failures)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
            }
        }

        private static void TestOuterIterationExecution()
        {
            using var harness = CreateHarness(innerIterations: 1);
            Workspace.SetReconstructionFrames(new List<ReconstructionFrame>());
            Workspace.SetReconstructionResults(new List<ReconstructionResult>());

            harness.Service.Run(maxIterationCount: 3,
                                stepSize: 0.35,
                                regularizationWeight: 5e-4,
                                excitationAmplitude: 1.0);

            if (!SpinWait.SpinUntil(() => !harness.Service.IsRunning, TimeSpan.FromSeconds(15)))
                throw new Exception("Convexification background run did not finish in time.");

            int producedResults = Workspace.GetReconstructionResults().Count;
            if (producedResults <= 1)
            {
                throw new Exception($"Background convexification run did not execute repeated outer cycles. Produced only {producedResults} result snapshot.");
            }
        }

        private static void TestBoundaryProxySanity()
        {
            using var harness = CreateHarness();
            var state = harness.RunPersistenceCycle();

            foreach (var frame in state.BoundaryData)
            {
                AssertFinite(frame.G0, "g0");
                AssertFinite(frame.S0, "s0");
                AssertFinite(frame.S1, "s1");
                AssertFinite(frame.B0, "b0");
                AssertFinite(frame.C0, "c0");

                if (frame.G0.Any(value => value <= 0.0))
                    throw new Exception("Positivity-shifted g0 contained a non-positive value.");
            }
        }

        private static void TestResidualTrend()
        {
            using var harness = CreateHarness();
            var state = harness.RunPersistenceCycle();

            if (state.ObjectiveHistory.Count < 2)
                throw new Exception("Objective history did not contain multiple inner iterations.");

            for (int index = 1; index < state.ObjectiveHistory.Count; index++)
            {
                double previous = state.ObjectiveHistory[index - 1];
                double current = state.ObjectiveHistory[index];
                if (current > previous * 1.01 + 1e-9)
                {
                    throw new Exception($"Convexification objective increased too much between inner iterations {index - 1} and {index}: {previous:G6} -> {current:G6}.");
                }
            }
        }

        private static void TestCoefficientRecoverySanity()
        {
            using var harness = CreateHarness(inhomogeneityValue: 3.0, layers: 4, boundaryVertexCount: 32);
            var state = harness.RunPersistenceCycle();

            var (interiorVertices, boundaryVertices) = PartitionVerticesByRadius(harness.Mesh);
            double interiorMagnitude = interiorVertices.Average(vertexId => Math.Abs(state.RecoveredCoefficientField.GetPotential(vertexId)));
            double boundaryMagnitude = boundaryVertices.Average(vertexId => Math.Abs(state.RecoveredCoefficientField.GetPotential(vertexId)));

            if (interiorMagnitude <= 0.01)
                throw new Exception("Recovered coefficient a(x) had negligible interior magnitude.");
            if (interiorMagnitude < 0.15 * Math.Max(boundaryMagnitude, 1e-12))
                throw new Exception("Recovered coefficient a(x) remained concentrated near the boundary.");
        }

        private static void TestScaleRecoverySanity()
        {
            using var harness = CreateHarness(inhomogeneityValue: 3.0, layers: 4, boundaryVertexCount: 32);
            var state = harness.RunPersistenceCycle();

            var (interiorVertices, _) = PartitionVerticesByRadius(harness.Mesh);
            var values = state.RecoveredScaleField.Potentials.Values.ToList();
            double spread = values.Max() - values.Min();
            double interiorDeviation = interiorVertices
                .Average(vertexId => Math.Abs(state.RecoveredScaleField.GetPotential(vertexId) - 1.0));

            if (values.Min() <= harness.Parameters.ConvexificationOptions.MinimumScale * 0.999)
                throw new Exception("Recovered V field collapsed to the minimum scale floor.");
            if (spread <= 0.01)
                throw new Exception("Recovered V field was nearly constant and lost interior structure.");
            if (interiorDeviation <= 0.01)
                throw new Exception("Recovered V field did not react in the interior of the domain.");
        }

        private static void TestHomogeneousConductivityRecovery()
        {
            using var harness = CreateHarness();
            var result = harness.Service.RunFullReconstructionCycleAsync(stepSize: 0.35,
                                                                         regularizationWeight: 5e-4,
                                                                         excitationAmplitude: 1.0)
                .GetAwaiter()
                .GetResult()
                ?? throw new Exception("Convexification service returned no reconstruction result.");

            var values = result.ReconstructedConductivityDistribution.Conductivities.Values.ToList();
            if (values.Count == 0)
                throw new Exception("Recovered conductivity distribution was empty.");
            if (values.Any(value => !double.IsFinite(value)))
                throw new Exception("Recovered conductivity distribution contained a non-finite value.");

            double meanAbsoluteDeviation = values.Average(value => Math.Abs(value - 1.0));
            if (meanAbsoluteDeviation > 0.35)
            {
                throw new Exception($"Homogeneous conductivity recovery drifted too far from 1. Mean absolute deviation: {meanAbsoluteDeviation:G4}.");
            }
        }

        private static void TestInteriorAnomalyRecovery()
        {
            using var harness = CreateHarness(inhomogeneityValue: 3.0, layers: 4, boundaryVertexCount: 32);
            var state = harness.RunPersistenceCycle();

            var (interiorElements, boundaryElements) = PartitionElementsByRadius(harness.Mesh);
            double interiorDeviation = interiorElements
                .Average(elementId => Math.Abs(state.ReconstructedConductivity.GetConductivity(elementId) - 1.0));
            double boundaryDeviation = boundaryElements
                .Average(elementId => Math.Abs(state.ReconstructedConductivity.GetConductivity(elementId) - 1.0));

            if (interiorDeviation <= 0.02)
                throw new Exception("Interior anomaly reconstruction remained too weak in the domain interior.");
            if (interiorDeviation < 0.25 * Math.Max(boundaryDeviation, 1e-12))
                throw new Exception("Recovered conductivity remained dominated by a boundary shell.");
        }

        private static void TestServiceIntegration()
        {
            using var harness = CreateHarness();
            if (!harness.Service.IsInitialized)
                throw new Exception("Convexification service did not initialise.");

            var result = harness.Service.RunFullReconstructionCycleAsync(stepSize: 0.35,
                                                                         regularizationWeight: 5e-4,
                                                                         excitationAmplitude: 1.0)
                .GetAwaiter()
                .GetResult()
                ?? throw new Exception("Convexification service returned no reconstruction result.");

            if (result.Frames.Count == 0)
                throw new Exception("Convexification service produced no reconstruction frames.");
            if (Workspace.GetReconstructionFrames().Count == 0)
                throw new Exception("Convexification frames were not published to the workspace.");
            if (Workspace.GetReconstructionResults().Count == 0)
                throw new Exception("Convexification results were not published to the workspace.");
        }

        private static ConvexificationTestHarness CreateHarness(double inhomogeneityValue = 1.0,
                                                                int innerIterations = 8,
                                                                int layers = 3,
                                                                int boundaryVertexCount = 24)
        {
            var snapshot = WorkspaceSnapshot.Capture();

            try
            {
                Workspace.SetUseBlockConfiguration(false);
                Workspace.SetCompleteReconstructionConfiguration(null);
                Workspace.ClearImportedMeasurement();
                Workspace.SetMeasurementSource(MeasurementSourceOption.Simulated);
                Workspace.SetMeasurementPattern(null);
                Workspace.SetReconstructionFrames(new List<ReconstructionFrame>());
                Workspace.SetReconstructionResults(new List<ReconstructionResult>());

                var mesh = MeshFactory.CreateCircularFEMMesh(layers: layers,
                                                             boundaryFEMVertexCount: boundaryVertexCount,
                                                             electrodeCount: 8,
                                                             inhomogeneityValue: inhomogeneityValue,
                                                             nodesPerElectrode: 2,
                                                             electrodeLengthHint: 0.2);

                var originalDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);
                var initialDistribution = new ConductivityDistribution(
                    mesh.GetConductivityDistribution().Conductivities.Keys.ToDictionary(elementId => elementId, _ => 1.0));
                mesh.SetConductivityDistribution(new ConductivityDistribution(originalDistribution.Conductivities));

                var parameters = new ReconstructionRuntimeContext
                {
                    DifferentialEquationSolver = Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FEM,
                    NumericSolver = NumericSolver.GMRES,
                    DrivePattern = DrivePattern.Adjecent,
                    MeasurementSetup = ElectrodeMeasurementSetup.Active,
                    ContactImpedanceOhms = 0.05,
                    InitializationCurrentAmplitude = 1.0,
                    UsePotentialDifferences = false,
                    ConductivityMinimumBound = 0.2,
                    ConductivityMaximumBound = 4.0,
                    OriginalDistribution = new ConductivityDistribution(originalDistribution.Conductivities),
                    InitialDistribution = new ConductivityDistribution(initialDistribution.Conductivities),
                    ConvexificationOptions = new ConvexificationOptions
                    {
                        Lambda = 1.0,
                        InteriorResidualWeight = 8.0,
                        Beta = 2e-4,
                        Epsilon = 0.5,
                        D0 = 0.2,
                        PositivityMargin = 1e-3,
                        StepSize = 0.2,
                        MaxIterations = innerIterations,
                        Tolerance = 5e-6,
                        InnerGradientTolerance = 1e-7,
                        OuterIterations = 0,
                        OuterTolerance = 5e-4,
                        BoundaryDirichletWeight = 0.2,
                        BoundaryNeumannWeight = 0.04,
                        UsePeriodicDriveDerivative = true,
                        DerivativeSmoothingWindow = 3,
                        DerivativeSmoothingPasses = 1,
                        UsePeriodicDerivativeSmoothing = true,
                        AverageRecoveredCoefficientAcrossCycle = true,
                        CoefficientSmoothingWeight = 0.02,
                        SigmaRecoveryRegularization = 1e-5,
                        MinimumScale = 0.2,
                        VRecoveryResidualWeight = 12.0,
                        VRecoveryDirichletWeight = 3.0,
                        VRecoveryNeumannWeight = 0.6,
                        VRecoveryGradientWeight = 8e-3,
                        VRecoveryMassWeight = 1e-4
                    }
                };

                Workspace.SetReconstructionParameters(parameters);
                Workspace.SetDiscretization(mesh);
                Workspace.SetOriginalDiscretization(mesh.DeepCopy());
                Workspace.SetInitialDiscretization(mesh.DeepCopy());
                Workspace.SetOriginalConductivityDistribution(parameters.OriginalDistribution);
                Workspace.SetInitialConductivityDistribution(parameters.InitialDistribution);
                Workspace.SetElectrodeMeasurementSetup(parameters.MeasurementSetup);

                var logger = new WorkspaceLogger();
                var measurementService = new MeasurementService(new MeasurementPersistence(), logger);
                var persistence = new ConvexificationReconstructionPersistence(new ReconstructionRepository());
                var service = new ConvexificationReconstructionService(persistence, measurementService, logger);

                service.InitializeReconstruction(mesh, parameters, reinit: true);
                return new ConvexificationTestHarness(service, persistence, measurementService, mesh, parameters, snapshot);
            }
            catch
            {
                snapshot.Restore();
                throw;
            }
        }

        private readonly struct ConvexificationTestHarness : IDisposable
        {
            public ConvexificationTestHarness(ConvexificationReconstructionService service,
                                             ConvexificationReconstructionPersistence persistence,
                                             MeasurementService measurementService,
                                             FEMMesh mesh,
                                             ReconstructionRuntimeContext parameters,
                                             WorkspaceSnapshot snapshot)
            {
                Service = service;
                Persistence = persistence;
                MeasurementService = measurementService;
                Mesh = mesh;
                Parameters = parameters;
                Snapshot = snapshot;
            }

            public ConvexificationReconstructionService Service { get; }
            public ConvexificationReconstructionPersistence Persistence { get; }
            public MeasurementService MeasurementService { get; }
            public FEMMesh Mesh { get; }
            public ReconstructionRuntimeContext Parameters { get; }
            private WorkspaceSnapshot Snapshot { get; }

            public ConvexificationState RunPersistenceCycle()
            {
                MeasurementService.SyncMeasurementSource();
                MeasurementService.EnsureMeasurements(Parameters.InitializationCurrentAmplitude);

                var frames = MeasurementService.GetAllMeasurements()
                    .Select(frame => (double[])frame.Clone())
                    .ToList();
                var stepIndices = Enumerable.Range(0, frames.Count).ToList();
                var electrodes = Mesh.ElectrodesTyped
                    .Where(electrode => !electrode.IsVirtual)
                    .Cast<Electrode>()
                    .ToList();
                if (electrodes.Count == 0)
                    electrodes = Mesh.ElectrodesTyped.Cast<Electrode>().ToList();

                var representation = Parameters.UsePotentialDifferences
                    ? MeasurementRepresentation.PotentialDifference
                    : MeasurementRepresentation.Amplitude;
                var description = DrivePatternStrategyProvider.GetStrategy(Parameters.DrivePattern, Parameters.DrivePatternSkip)
                    .BuildDescription(Math.Max(1, electrodes.Count),
                                      representation,
                                      Parameters.MeasurementSetup);

                var cycle = new EITMeasurement(frames,
                                               MeasurementService.CurrentPattern,
                                               description,
                                               stepIndices)
                {
                    CurrentAmplitude = MeasurementService.RealMeasurementAmplitude ?? Parameters.InitializationCurrentAmplitude
                };

                return Persistence.RunReconstructionCycle(cycle);
            }

            public void Dispose()
            {
                Service.Stop();
                Snapshot.Restore();
            }
        }

        private sealed class WorkspaceSnapshot
        {
            private ReconstructionRuntimeContext Parameters { get; init; } = new();
            private Utility.Classes.Discretizer.IDiscretization? Discretization { get; init; }
            private Utility.Classes.Discretizer.IDiscretization? OriginalDiscretization { get; init; }
            private Utility.Classes.Discretizer.IDiscretization? InitialDiscretization { get; init; }
            private List<ReconstructionResult> Results { get; init; } = [];
            private List<ReconstructionFrame> Frames { get; init; } = [];
            private ConductivityDistribution? OriginalConductivity { get; init; }
            private ConductivityDistribution? InitialConductivity { get; init; }
            private MeasurementSourceOption MeasurementSource { get; init; }
            private ElectrodeMeasurementSetup MeasurementSetup { get; init; }
            private MeasurementPattern? MeasurementPattern { get; init; }
            private EITMeasurement? ImportedMeasurement { get; init; }
            private string? ImportedMeasurementLabel { get; init; }
            private bool UseBlockConfiguration { get; init; }
            private Utility.Classes.Configurations.ReconstructionConfiguration.CompleteReconstructionConfiguration? Configuration { get; init; }

            public static WorkspaceSnapshot Capture()
            {
                return new WorkspaceSnapshot
                {
                    Parameters = Workspace.GetReconstructionParameters(),
                    Discretization = Workspace.GetDiscretization(),
                    OriginalDiscretization = Workspace.GetOriginalDiscretization(),
                    InitialDiscretization = Workspace.GetInitialDiscretization(),
                    Results = new List<ReconstructionResult>(Workspace.GetReconstructionResults()),
                    Frames = new List<ReconstructionFrame>(Workspace.GetReconstructionFrames()),
                    OriginalConductivity = Workspace.GetOriginalConductivityDistribution(),
                    InitialConductivity = Workspace.GetInitialConductivityDistribution(),
                    MeasurementSource = Workspace.GetMeasurementSource(),
                    MeasurementSetup = Workspace.GetElectrodeMeasurementSetup(),
                    MeasurementPattern = Workspace.GetMeasurementPattern(),
                    ImportedMeasurement = Workspace.GetImportedMeasurement(),
                    ImportedMeasurementLabel = Workspace.GetImportedMeasurementLabel(),
                    UseBlockConfiguration = Workspace.GetUseBlockConfiguration(),
                    Configuration = Workspace.GetCompleteReconstructionConfiguration()
                };
            }

            public void Restore()
            {
                Workspace.SetReconstructionParameters(Parameters);
                Workspace.SetDiscretization(Discretization);
                Workspace.SetOriginalDiscretization(OriginalDiscretization);
                Workspace.SetInitialDiscretization(InitialDiscretization);
                Workspace.SetReconstructionResults(Results);
                Workspace.SetReconstructionFrames(Frames);
                Workspace.SetOriginalConductivityDistribution(OriginalConductivity);
                Workspace.SetInitialConductivityDistribution(InitialConductivity);
                Workspace.SetUseBlockConfiguration(UseBlockConfiguration);
                Workspace.SetCompleteReconstructionConfiguration(Configuration);

                if (ImportedMeasurement != null)
                    Workspace.SetImportedMeasurement(ImportedMeasurement, ImportedMeasurementLabel);
                else
                    Workspace.ClearImportedMeasurement();

                Workspace.SetMeasurementSource(MeasurementSource);
                Workspace.SetElectrodeMeasurementSetup(MeasurementSetup);
                Workspace.SetMeasurementPattern(MeasurementPattern);
            }
        }

        private static void AssertFinite(IEnumerable<double> values, string name)
        {
            if (values.Any(value => !double.IsFinite(value)))
                throw new Exception($"{name} contained a non-finite value.");
        }

        private static (List<int> Interior, List<int> Boundary) PartitionVerticesByRadius(FEMMesh mesh)
        {
            var interior = new List<int>();
            var boundary = new List<int>();

            foreach (var vertex in mesh.Vertices)
            {
                double radius = Math.Sqrt(vertex.X * vertex.X + vertex.Y * vertex.Y);
                if (radius < 0.55)
                    interior.Add(vertex.GlobalId);
                else if (radius > 0.80)
                    boundary.Add(vertex.GlobalId);
            }

            if (interior.Count == 0 || boundary.Count == 0)
                throw new Exception("Failed to partition mesh vertices into interior and boundary subsets.");

            return (interior, boundary);
        }

        private static (List<int> Interior, List<int> Boundary) PartitionElementsByRadius(FEMMesh mesh)
        {
            var interior = new List<int>();
            var boundary = new List<int>();

            foreach (var element in mesh.ElementsTyped.Cast<FEMElement>())
            {
                double cx = element.Vertices.Average(vertex => vertex.X);
                double cy = element.Vertices.Average(vertex => vertex.Y);
                double radius = Math.Sqrt(cx * cx + cy * cy);

                if (radius < 0.45)
                    interior.Add(element.Id);
                else if (radius > 0.75)
                    boundary.Add(element.Id);
            }

            if (interior.Count == 0 || boundary.Count == 0)
                throw new Exception("Failed to partition mesh elements into interior and boundary subsets.");

            return (interior, boundary);
        }
    }
}
