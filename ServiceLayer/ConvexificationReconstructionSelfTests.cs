using System.Diagnostics;
using BusinessLayer;
using DataAccessLayer;
using Utility.Classes;
using Utility.Classes.Application;
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
            Try("Convexification homogeneous conductivity recovery", TestHomogeneousConductivityRecovery, failures);
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

        private static void TestHomogeneousConductivityRecovery()
        {
            using var harness = CreateHarness();
            var result = harness.Service.RunFullReconstructionCycleAsync(stepSize: 0.5,
                                                                         regularizationWeight: 1e-2,
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

        private static void TestServiceIntegration()
        {
            using var harness = CreateHarness();
            if (!harness.Service.IsInitialized)
                throw new Exception("Convexification service did not initialise.");

            var result = harness.Service.RunFullReconstructionCycleAsync(stepSize: 0.5,
                                                                         regularizationWeight: 1e-2,
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

        private static ConvexificationTestHarness CreateHarness()
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

                var mesh = MeshFactory.CreateCircularFEMMesh(layers: 3,
                                                             boundaryFEMVertexCount: 24,
                                                             electrodeCount: 8,
                                                             inhomogeneityValue: 1.0,
                                                             nodesPerElectrode: 2,
                                                             electrodeLengthHint: 0.2);

                var homogeneous = new ConductivityDistribution(
                    mesh.GetConductivityDistribution().Conductivities.Keys.ToDictionary(elementId => elementId, _ => 1.0));
                mesh.SetConductivityDistribution(new ConductivityDistribution(homogeneous.Conductivities));

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
                    ConductivityMaximumBound = 2.5,
                    OriginalDistribution = new ConductivityDistribution(homogeneous.Conductivities),
                    InitialDistribution = new ConductivityDistribution(homogeneous.Conductivities),
                    ConvexificationOptions = new ConvexificationOptions
                    {
                        Lambda = 1.0,
                        Beta = 1e-2,
                        Epsilon = 0.5,
                        D0 = 0.2,
                        PositivityMargin = 1e-3,
                        StepSize = 0.5,
                        MaxIterations = 6,
                        Tolerance = 1e-4,
                        UsePeriodicDriveDerivative = true,
                        AverageRecoveredCoefficientAcrossCycle = true,
                        SigmaRecoveryRegularization = 1e-5,
                        MinimumScale = 0.2
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
                var service = new ConvexificationReconstructionService(new ConvexificationReconstructionPersistence(new ReconstructionRepository()),
                                                                       measurementService,
                                                                       logger);

                service.InitializeReconstruction(mesh, parameters, reinit: true);
                return new ConvexificationTestHarness(service, snapshot);
            }
            catch
            {
                snapshot.Restore();
                throw;
            }
        }

        private readonly struct ConvexificationTestHarness : IDisposable
        {
            public ConvexificationTestHarness(ConvexificationReconstructionService service, WorkspaceSnapshot snapshot)
            {
                Service = service;
                Snapshot = snapshot;
            }

            public ConvexificationReconstructionService Service { get; }
            private WorkspaceSnapshot Snapshot { get; }

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
    }
}
