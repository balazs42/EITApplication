using BusinessLayer;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction.Convexification;
using Utility.Classes.ReconstructionParameters;
using Utility.Logger;
using Utility.Exports;

namespace ServiceLayer
{
    /// <summary>
    /// Service wrapper for the convexification reconstruction path.
    /// The implementation reuses the existing workspace and measurement-service
    /// conventions, but executes conductivity updates only after a coherent
    /// drive-pattern cycle has been assembled.
    /// </summary>
    public sealed class ConvexificationReconstructionService : ReconstructionServiceBase, IConvexificationReconstructionService
    {
        private readonly ConvexificationReconstructionPersistence _persistence;
        private readonly IMeasurementService _measurementService;
        private readonly ILogger _logger;

        private ReconstructionRuntimeContext? _runtimeContext;
        private ConductivityDistribution? _currentSigma;
        private bool _initialized;
        private int _frameIndex;
        private ReconstructionResult? _lastCycleResult;

        public override bool IsInitialized => _initialized && _runtimeContext != null;

        public ConvexificationReconstructionService(ConvexificationReconstructionPersistence persistence,
                                                    IMeasurementService measurementService,
                                                    ILogger logger)
        {
            _persistence = persistence;
            _measurementService = measurementService;
            _logger = logger;

            _persistence.ReconstructionFrameUpdated += OnPersistenceFrameUpdated;
            _persistence.ReconstructionUpdated += OnPersistenceReconstructionUpdated;
        }

        /// <inheritdoc />
        public void Initialize()
        {
            var discretization = Workspace.GetDiscretization()
                                ?? throw new InvalidOperationException("No discretization is available in the workspace.");
            var parameters = Workspace.GetReconstructionParameters();
            InitializeReconstruction(discretization, parameters, false);
        }

        /// <inheritdoc />
        public override void InitializeReconstruction(IDiscretization discretization, ReconstructionRuntimeContext parameters, bool reinit)
        {
            if (discretization is not FEMMesh femMesh)
                throw new InvalidOperationException("Convexification reconstruction currently requires a FEM mesh.");

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            parameters.RuntimeMesh = femMesh;
            _persistence.Initialize(parameters, reinit);
            _runtimeContext = _persistence.RuntimeContext
                              ?? throw new InvalidOperationException("Failed to initialise the convexification runtime context.");

            Workspace.SetDiscretization(femMesh);

            var originalSnapshot = new ConductivityDistribution((_runtimeContext.OriginalDistribution
                                                                 ?? femMesh.GetConductivityDistribution()).Conductivities);
            var initialSnapshot = new ConductivityDistribution((_runtimeContext.InitialDistribution
                                                                ?? femMesh.GetConductivityDistribution()).Conductivities);

            _currentSigma = new ConductivityDistribution(initialSnapshot.Conductivities);

            Workspace.SetOriginalConductivityDistribution(originalSnapshot);
            Workspace.SetInitialConductivityDistribution(initialSnapshot);
            Workspace.SetElectrodeMeasurementSetup(_runtimeContext.MeasurementSetup);

            _measurementService.Initialize(femMesh,
                                           parameters,
                                           parameters.DrivePattern,
                                           () => _persistence.DifferentialEquationSolver,
                                           originalSnapshot);
            _measurementService.SyncMeasurementSource();

            Workspace.SetReconstructionFrames(new List<ReconstructionFrame>());
            Workspace.SetReconstructionResults(new List<ReconstructionResult>());
            ClearPublishedResults();
            _persistence.ResetResults();
            _frameIndex = 0;
            _lastCycleResult = null;
            _initialized = true;
        }

        /// <inheritdoc />
        public override Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                                     double regularizationWeight,
                                                                                     double excitationAmplitude)
            => Task.Run(() => ExecuteFullCycle(stepSize, regularizationWeight, excitationAmplitude));

        protected override Task<ReconstructionResult?> StepCoreAsync(double stepSize,
                                                                     double regularizationWeight,
                                                                     double excitationAmplitude)
            => Task.Run(() => ExecuteFullCycle(stepSize, regularizationWeight, excitationAmplitude));

        protected override async Task RunCoreAsync(int maxIterationCount,
                                                   double stepSize,
                                                   double regularizationWeight,
                                                   double excitationAmplitude,
                                                   CancellationToken cancellationToken)
        {
            await WaitWhilePausedAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            _ = ExecuteFullCycle(stepSize, regularizationWeight, excitationAmplitude);

            if (VisualizeIterations)
                await Task.Yield();
        }

        private ReconstructionResult? ExecuteFullCycle(double stepSize,
                                                       double regularizationWeight,
                                                       double excitationAmplitude)
        {
            EnsureInitialized();
            ApplyTuning(stepSize, regularizationWeight);

            _measurementService.SyncMeasurementSource();
            _measurementService.EnsureMeasurements(excitationAmplitude);

            var measurements = _measurementService.GetAllMeasurements();
            if (measurements.Count == 0)
                return null;

            var stepIndices = Enumerable.Range(0, measurements.Count).ToList();
            var cycle = CreateMeasurementCycle(measurements.Select(frame => (double[])frame.Clone()).ToList(),
                                               stepIndices,
                                               excitationAmplitude);

            var result = ExecuteMeasurementCycle(cycle);
            _frameIndex += measurements.Count;
            return result;
        }

        private ReconstructionResult ExecuteMeasurementCycle(EITMeasurement cycle)
        {
            if (_runtimeContext == null)
                throw new InvalidOperationException("Convexification runtime context is not available.");

            _lastCycleResult = null;
            var state = _persistence.RunReconstructionCycle(cycle);
            foreach (var warning in state.Warnings)
                _logger.LogWarning(warning);

            var updated = new ConductivityDistribution(state.ReconstructedConductivity.Conductivities);
            _currentSigma = updated;
            return _lastCycleResult
                   ?? Workspace.GetReconstructionResults().LastOrDefault()
                   ?? new ReconstructionResult((_runtimeContext.RuntimeMesh
                                                ?? throw new InvalidOperationException("Runtime mesh is not available.")).GetDiscretization(),
                                               _runtimeContext.OriginalDistribution ?? updated,
                                               _runtimeContext.InitialDistribution ?? updated,
                                               updated,
                                               state.Frames.ToList());
        }

        private EITMeasurement CreateMeasurementCycle(List<double[]> frames,
                                                      List<int> stepIndices,
                                                      double excitationAmplitude)
        {
            if (_runtimeContext?.RuntimeMesh == null)
                throw new InvalidOperationException("Convexification runtime mesh is not available.");

            var electrodes = _runtimeContext.RuntimeMesh.ElectrodesTyped
                .Where(electrode => !electrode.IsVirtual)
                .Cast<Electrode>()
                .ToList();
            if (electrodes.Count == 0)
                electrodes = _runtimeContext.RuntimeMesh.ElectrodesTyped.Cast<Electrode>().ToList();

            var representation = _runtimeContext.UsePotentialDifferences
                ? MeasurementRepresentation.PotentialDifference
                : MeasurementRepresentation.Amplitude;
            var description = DrivePatternStrategyProvider.GetStrategy(_runtimeContext.DrivePattern,
                                                                       _runtimeContext.DrivePatternSkip)
                .BuildDescription(Math.Max(1, electrodes.Count),
                                  representation,
                                  _runtimeContext.MeasurementSetup);

            return new EITMeasurement(frames,
                                      _measurementService.CurrentPattern,
                                      description,
                                      stepIndices)
            {
                CurrentAmplitude = _measurementService.RealMeasurementAmplitude ?? excitationAmplitude
            };
        }

        private void ApplyTuning(double stepSize, double regularizationWeight)
        {
            if (_runtimeContext == null)
                return;

            if (stepSize > 0.0)
                _runtimeContext.ConvexificationOptions.StepSize = stepSize;
            if (regularizationWeight > 0.0)
                _runtimeContext.ConvexificationOptions.Beta = regularizationWeight;
        }

        private void EnsureInitialized()
        {
            if (_initialized)
                return;

            Initialize();
        }

        private void OnPersistenceFrameUpdated(object? sender, ReconstructionFrame frame)
        {
            Workspace.AddReconstructionFrameToWorkspace(frame);
            PublishFrame(frame);
        }

        private void OnPersistenceReconstructionUpdated(object? sender, ReconstructionResult result)
        {
            _lastCycleResult = result;
            _currentSigma = new ConductivityDistribution(result.ReconstructedConductivityDistribution.Conductivities);
            Workspace.AddReconstructionResultToWorkspace(result);
            PublishResult(result);
        }

        public override void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters)
        {
            try
            {
                _persistence.SaveReconstruction(frames, name, parameters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                throw;
            }
        }

        public override IEnumerable<ReconstructionInfo> GetReconstructions()
        {
            try
            {
                return _persistence.GetReconstructions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                throw;
            }
        }

        public override List<ReconstructionResult> LoadReconstruction(string filePath)
        {
            try
            {
                var results = _persistence.LoadReconstruction(filePath);
                Workspace.SetReconstructionResults(results);
                Workspace.SetReconstructionFrames([.. results.SelectMany(result => result.Frames)]);
                Workspace.SetInitialConductivityDistribution(results.FirstOrDefault()?.InitialConductivitiyDistribution);
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                throw;
            }
        }
    }
}
