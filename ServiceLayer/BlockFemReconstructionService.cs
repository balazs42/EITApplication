using BusinessLayer;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction;
using Utility.Classes.ReconstructionParameters;
using Utility.Logger;

namespace ServiceLayer
{
    /// <summary>
    /// Wires the block-based FEM reconstruction persistence into the application layer.
    /// Responsible for preparing measurements, executing the block persistence step and
    /// emitting intermediate frames and aggregated results for UI consumption.
    /// </summary>
    public class BlockFemReconstructionService : IBlockFemReconstructionService
    {
        private readonly BlockFemReconstructionPersistence _persistence;
        private readonly IMeasurementService _measurementService;
        private readonly ILogger _logger;

        private ReconstructionRuntimeContext? _runtimeContext;
        private bool _initialized;

        /// <inheritdoc />
        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;

        /// <inheritdoc />
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        public BlockFemReconstructionService(BlockFemReconstructionPersistence persistence,
                                             IMeasurementService measurementService,
                                             ILogger logger)
        {
            _persistence = persistence;
            _measurementService = measurementService;
            _logger = logger;
        }

        /// <inheritdoc />
        public void Initialize()
        {
            var configuration = Workspace.GetCompleteReconstructionConfiguration()
                                ?? throw new InvalidOperationException("Block reconstruction requires a configured reconstruction canvas.");

            _persistence.Initialize(configuration);
            _runtimeContext = _persistence.RuntimeContext
                              ?? throw new InvalidOperationException("Failed to materialize reconstruction runtime context.");

            var discretization = Workspace.GetDiscretization().DeepCopy();

            if (discretization is null)
                throw new NullReferenceException("Discretization was null during initialization of block reconstruction service. Check code!");

            //Workspace.SetDiscretization(_runtimeContext.Mesh);

            // Store independent snapshots of the original and initial distributions so later updates
            // to the mesh conductivity do not mutate these references through shared dictionaries.
            var originalSnapshot = new ConductivityDistribution(_runtimeContext.OriginalDistribution.Conductivities);
            var initialSnapshot = new ConductivityDistribution(_runtimeContext.InitialDistribution.Conductivities);

            Workspace.SetOriginalConductivityDistribution(originalSnapshot);
            Workspace.SetInitialConductivityDistribution(initialSnapshot);
            Workspace.SetElectrodeMeasurementSetup(_runtimeContext.MeasurementSetup);

            var parameters = Workspace.GetReconstructionParameters();
            _measurementService.Initialize(discretization,
                                           parameters,
                                           parameters.DrivePattern,
                                           () => _persistence.DifferentialEquationSolver,
                                           _runtimeContext.OriginalDistribution);
            _measurementService.SyncMeasurementSource();

            Workspace.SetReconstructionFrames(new List<ReconstructionFrame>());
            Workspace.SetReconstructionResults(new List<ReconstructionResult>());
            _initialized = true;
        }

        /// <inheritdoc />
        public Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                           double regularizationWeight,
                                                                           double excitationAmplitude)
            => Task.Run(() => ExecuteReconstructionCycle(stepSize, regularizationWeight, excitationAmplitude));

        /// <inheritdoc />
        public Task<ReconstructionResult?> StepReconstructionAsync(double stepSize,
                                                                    double regularizationWeight,
                                                                    double excitationAmplitude)
            => Task.Run(() => ExecuteReconstructionCycle(stepSize, regularizationWeight, excitationAmplitude));

        private ReconstructionResult? ExecuteReconstructionCycle(double stepSize,
                                                                  double regularizationWeight,
                                                                  double excitationAmplitude)
        {
            if (!_initialized)
                Initialize();
            if (_runtimeContext == null)
                throw new InvalidOperationException("Reconstruction runtime context is not available.");

            try
            {
                var measurement = PrepareMeasurement(excitationAmplitude);
                var frames = _persistence.Step(measurement);

                foreach (var frame in frames)
                {
                    Workspace.AddReconstructionFrameToWorkspace(frame);
                    ReconstructionFrameUpdated?.Invoke(this, frame);
                }

                var mesh = Workspace.GetDiscretization();
                var previous = mesh.GetConductivityDistribution();
                var updated = ApplyGradientUpdate(frames, previous, stepSize, regularizationWeight);
                if (updated == null)
                    return null;

                var result = new ReconstructionResult(mesh.GetDiscretization(),
                                                      _runtimeContext.OriginalDistribution,
                                                      previous,
                                                      updated,
                                                      frames);

                Workspace.AddReconstructionResultToWorkspace(result);
                ReconstructionUpdated?.Invoke(this, result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                throw;
            }
        }

        private EITMeasurement PrepareMeasurement(double excitationAmplitude)
        {
            if (_runtimeContext == null)
                throw new InvalidOperationException("Reconstruction runtime context is not available.");

            _measurementService.EnsureMeasurements(excitationAmplitude);

            var discretization = Workspace.GetDiscretization();

            if (discretization == null)
                throw new NullReferenceException("Discretization was null during measurement preparation, check code!");

            var electrodes = discretization.GetElectrodes().Cast<Electrode>().ToList();
            var preparedFrames = new List<double[]>();

            foreach (var frame in _measurementService.GetAllMeasurements())
            {
                var prepared = _measurementService.PrepareMeasurementFrame(frame, electrodes);
                preparedFrames.Add(prepared);
            }

            var measurement = new EITMeasurement(preparedFrames, _measurementService.CurrentPattern)
            {
                CurrentAmplitude = _measurementService.RealMeasurementAmplitude ?? excitationAmplitude
            };

            return measurement;
        }

        private ConductivityDistribution? ApplyGradientUpdate(IReadOnlyList<ReconstructionFrame> frames,
                                                              ConductivityDistribution currentSigma,
                                                              double stepSize,
                                                              double regularizationWeight)
        {
            if (_runtimeContext == null)
                return null;
            if (frames.Count == 0)
                return null;

            var optimizerGradients = AggregateOptimizerGradients(frames, regularizationWeight);
            var updated = ApplyOptimizers(currentSigma, optimizerGradients, stepSize);
            updated = ConductivityClipper.Clip(updated);

            _persistence.UpdateCurrentDistribution(updated);
            Workspace.SetInitialConductivityDistribution(updated);
            return updated;
        }

        private static Dictionary<string, ConductivityDistribution> AggregateOptimizerGradients(IReadOnlyList<ReconstructionFrame> frames,
                                                                                               double regularizationWeight)
        {
            var gradientAccum = new Dictionary<string, Dictionary<int, double>>();
            var regAccum = new Dictionary<string, Dictionary<int, double>>();

            foreach (var frame in frames)
            {
                foreach (var gradientEntry in frame.OptimizerGradients)
                {
                    if (!gradientAccum.TryGetValue(gradientEntry.Key, out var dict))
                    {
                        dict = new Dictionary<int, double>();
                        gradientAccum[gradientEntry.Key] = dict;
                    }

                    foreach (var kvp in gradientEntry.Value.Conductivities)
                    {
                        dict[kvp.Key] = dict.TryGetValue(kvp.Key, out var existing)
                            ? existing + kvp.Value
                            : kvp.Value;
                    }
                }

                foreach (var regEntry in frame.OptimizerRegularizations)
                {
                    if (!regAccum.TryGetValue(regEntry.Key, out var dict))
                    {
                        dict = new Dictionary<int, double>();
                        regAccum[regEntry.Key] = dict;
                    }

                    foreach (var kvp in regEntry.Value.Conductivities)
                    {
                        dict[kvp.Key] = dict.TryGetValue(kvp.Key, out var existing)
                            ? existing + kvp.Value
                            : kvp.Value;
                    }
                }
            }

            int frameCount = Math.Max(1, frames.Count);
            var result = new Dictionary<string, ConductivityDistribution>();

            foreach (var optimizerId in gradientAccum.Keys.Union(regAccum.Keys))
            {
                var gradDict = gradientAccum.TryGetValue(optimizerId, out var g) ? g : new Dictionary<int, double>();
                var regDict = regAccum.TryGetValue(optimizerId, out var r) ? r : new Dictionary<int, double>();

                var combined = new Dictionary<int, double>();
                foreach (var kvp in gradDict)
                {
                    double reg = regDict.TryGetValue(kvp.Key, out var regVal) ? regVal : 0.0;
                    combined[kvp.Key] = (kvp.Value - regularizationWeight * reg) / frameCount;
                }

                foreach (var kvp in regDict)
                {
                    if (combined.ContainsKey(kvp.Key))
                        continue;
                    combined[kvp.Key] = (-regularizationWeight * kvp.Value) / frameCount;
                }

                result[optimizerId] = new ConductivityDistribution(combined);
            }

            return result;
        }

        private ConductivityDistribution ApplyOptimizers(ConductivityDistribution currentSigma,
                                                         IReadOnlyDictionary<string, ConductivityDistribution> optimizerGradients,
                                                         double stepSize)
        {
            if (_runtimeContext == null || _runtimeContext.NumericOptimizers == null || _runtimeContext.NumericOptimizers.Count == 0)
                return currentSigma;

            if (_runtimeContext.NumericOptimizers.Count == 1)
            {
                var optimizer = _runtimeContext.NumericOptimizers[0];
                var gradient = optimizerGradients.Values.FirstOrDefault() ?? new ConductivityDistribution(new Dictionary<int, double>());
                return optimizer.numericOptimizer.OptimizationStep(currentSigma, gradient, stepSize);
            }

            var weightedSum = new Dictionary<int, double>();
            double totalWeight = 0.0;

            foreach (var (id, weight, optimizer) in _runtimeContext.NumericOptimizers)
            {
                var gradient = optimizerGradients.TryGetValue(id, out var specific)
                    ? specific
                    : optimizerGradients.Values.FirstOrDefault() ?? new ConductivityDistribution(new Dictionary<int, double>());

                var candidate = optimizer.OptimizationStep(currentSigma, gradient, stepSize);
                foreach (var kvp in candidate.Conductivities)
                {
                    weightedSum[kvp.Key] = weightedSum.TryGetValue(kvp.Key, out var existing)
                        ? existing + weight * kvp.Value
                        : weight * kvp.Value;
                }

                totalWeight += weight;
            }

            if (totalWeight <= double.Epsilon)
                return currentSigma;

            var combined = weightedSum.ToDictionary(kvp => kvp.Key, kvp => kvp.Value / totalWeight);
            return new ConductivityDistribution(combined);
        }
    }
}
