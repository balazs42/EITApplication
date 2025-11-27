using BusinessLayer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Measurement;
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

            Workspace.SetDiscretization(_runtimeContext.Mesh);
            Workspace.SetOriginalConductivityDistribution(_runtimeContext.OriginalDistribution);
            Workspace.SetInitialConductivityDistribution(_runtimeContext.InitialDistribution);
            Workspace.SetElectrodeMeasurementSetup(_runtimeContext.MeasurementSetup);

            var parameters = Workspace.GetReconstructionParameters();
            _measurementService.Initialize(_runtimeContext.Mesh,
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

                var mesh = _runtimeContext.Mesh;
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

            var electrodes = _runtimeContext.Mesh.GetElectrodes().Cast<Electrode>().ToList();
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

            var aggregated = AggregateGradient(frames, regularizationWeight);
            var updated = ApplyOptimizers(currentSigma, aggregated, stepSize);
            updated = ConductivityClipper.Clip(updated);

            _persistence.UpdateCurrentDistribution(updated);
            Workspace.SetInitialConductivityDistribution(updated);
            return updated;
        }

        private static ConductivityDistribution AggregateGradient(IReadOnlyList<ReconstructionFrame> frames,
                                                                  double regularizationWeight)
        {
            var accumulator = new Dictionary<int, double>();
            foreach (var frame in frames)
            {
                foreach (var kvp in frame.ConductivityGradient.Conductivities)
                {
                    double reg = frame.CalculatedRegularization?.GetConductivity(kvp.Key) ?? 0.0;
                    double contribution = kvp.Value - regularizationWeight * reg;
                    accumulator[kvp.Key] = accumulator.TryGetValue(kvp.Key, out var existing)
                        ? existing + contribution
                        : contribution;
                }
            }

            int frameCount = Math.Max(1, frames.Count);
            var averaged = accumulator.ToDictionary(kvp => kvp.Key, kvp => kvp.Value / frameCount);
            return new ConductivityDistribution(averaged);
        }

        private ConductivityDistribution ApplyOptimizers(ConductivityDistribution currentSigma,
                                                         ConductivityDistribution gradient,
                                                         double stepSize)
        {
            if (_runtimeContext == null || _runtimeContext.NumericOptimizers == null || _runtimeContext.NumericOptimizers.Count == 0)
                return currentSigma;

            if (_runtimeContext.NumericOptimizers.Count == 1)
                return _runtimeContext.NumericOptimizers[0].numericOptimizer.OptimizationStep(currentSigma, gradient, stepSize);

            var weightedSum = new Dictionary<int, double>();
            double totalWeight = 0.0;

            foreach (var (weight, optimizer) in _runtimeContext.NumericOptimizers)
            {
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
