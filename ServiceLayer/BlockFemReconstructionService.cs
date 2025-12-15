using BusinessLayer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
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
        // Backing persistence that implements the actual FEM reconstruction step logic
        private readonly BlockFemReconstructionPersistence _persistence;
        // Provides measurement frame acquisition, preparation and pattern handling
        private readonly IMeasurementService _measurementService;
        // Cross-cutting logging
        private readonly ILogger _logger;

        // Materialized runtime context (mesh, optimizers, etc.) produced by persistence initialization
        private ReconstructionRuntimeContext? _runtimeContext;
        // Tracks service-level initialization to avoid redundant setup work
        private bool _initialized;
        // Monotonically increasing index of frames emitted so far (across cycles)
        private int _frameIndex;
        // Buffer to accumulate frames of the current drive-pattern cycle; flushed into a result when the cycle completes
        private readonly List<ReconstructionFrame> _cycleFrames = new();

        /// <inheritdoc />
        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;

        /// <inheritdoc />
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        /// <inheritdoc />
        public bool VisualizeIterations { get; set; } = true;

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
            // 1) Pull the full block-based reconstruction configuration from the workspace.
            //    The service depends on this configuration to construct runtime components in the persistence layer.
            var configuration = Workspace.GetCompleteReconstructionConfiguration()
                                ?? throw new InvalidOperationException("Block reconstruction requires a configured reconstruction canvas.");

            // 2) Initialize the persistence with the configuration which materializes runtime objects
            //    (mesh, solvers, optimizers, regularizers, etc.) and exposes them via the runtime context.
            _persistence.Initialize(configuration);
            _runtimeContext = _persistence.RuntimeContext
                              ?? throw new InvalidOperationException("Failed to materialize reconstruction runtime context.");

            // 3) Ensure we have a runtime FEM mesh to drive measurements and forward/adjoint solves.
            var runtimeMesh = _runtimeContext.RuntimeMesh
                              ?? throw new InvalidOperationException("Runtime mesh missing from reconstruction context.");

            // 4) Register the discretization in the global workspace so downstream features (UI, exports)
            //    can query mesh and state consistently.
            Workspace.SetDiscretization(runtimeMesh);

            // 5) Take defensive snapshots of the original and initial conductivity distributions.
            //    This avoids accidental aliasing: updates to mesh conductivity should not mutate these snapshots.
            var originalSnapshot = new ConductivityDistribution(_runtimeContext.OriginalDistribution.Conductivities);
            var initialSnapshot = new ConductivityDistribution(_runtimeContext.InitialDistribution.Conductivities);

            // 6) Publish baseline distributions and measurement setup to the workspace.
            Workspace.SetOriginalConductivityDistribution(originalSnapshot);
            Workspace.SetInitialConductivityDistribution(initialSnapshot);
            Workspace.SetElectrodeMeasurementSetup(_runtimeContext.MeasurementSetup);

            // 7) Prepare the measurement service with the mesh and reconstruction parameters.
            //    We pass an accessor to the DE solver so the measurement pipeline can reuse it if needed.
            var parameters = Workspace.GetReconstructionParameters();
            _measurementService.Initialize(runtimeMesh,
                                           parameters,
                                           parameters.DrivePattern,
                                           () => _persistence.DifferentialEquationSolver,
                                           _runtimeContext.OriginalDistribution);

            // 8) Sync measurement source (e.g., hardware, file, or simulated) with the current runtime setup.
            _measurementService.SyncMeasurementSource();

            // 9) Reset UI-facing collections and internal buffers for a fresh session.
            Workspace.SetReconstructionFrames(new List<ReconstructionFrame>());
            Workspace.SetReconstructionResults(new List<ReconstructionResult>());
            _cycleFrames.Clear();
            _frameIndex = 0;
            _initialized = true;
        }

        /// <inheritdoc />
        public Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                           double regularizationWeight,
                                                                           double excitationAmplitude)
            => Task.Run(() =>
            {
                // Execute exactly one complete drive-pattern cycle of reconstruction.
                // Each call to ExecuteReconstructionStep processes one measurement frame and may emit a result
                // only when the cycle completes. We return the last non-null result from the loop.
                ReconstructionResult? result = null;
                int cycleLength = Math.Max(1, _measurementService.FramesPerCycle);
                for (int i = 0; i < cycleLength; i++)
                {
                    result = ExecuteReconstructionStep(stepSize, regularizationWeight, excitationAmplitude);
                }

                return result;
            });

        /// <inheritdoc />
        public Task<ReconstructionResult?> StepReconstructionAsync(double stepSize,
                                                                    double regularizationWeight,
                                                                    double excitationAmplitude)
            => Task.Run(() => ExecuteReconstructionStep(stepSize, regularizationWeight, excitationAmplitude));

        // Processes a single frame worth of reconstruction work. Emits intermediate frames immediately via event
        // and returns a ReconstructionResult only when a full cycle worth of frames have been accumulated.
        private ReconstructionResult? ExecuteReconstructionStep(double stepSize,
                                                                  double regularizationWeight,
                                                                  double excitationAmplitude)
        {
            // Lazy-initialize to honor runtime edits in the UI without forcing a restart.
            if (!_initialized)
                Initialize();
            if (_runtimeContext == null)
                throw new InvalidOperationException("Reconstruction runtime context is not available.");

            try
            {
                // 1) Build an EITMeasurement that aligns the measurement frame(s) to the solver ordering
                //    and stores the drive-pattern step index for downstream boundary condition reconstruction.
                var measurement = PrepareMeasurement(excitationAmplitude);

                // 2) Execute the reconstruction step in the persistence layer. This returns one
                //    ReconstructionFrame per provided measurement frame (usually 1 here).
                var frames = _persistence.Step(measurement, _frameIndex);

                // 3) Surface each frame to the workspace and UI, and buffer them for the current cycle.
                foreach (var frame in frames)
                {
                    _cycleFrames.Add(frame);
                    PublishFrame(frame);
                }

                // 4) Advance the global frame counter by the number of frames produced.
                _frameIndex += frames.Count;

                // 5) If the current cycle is not complete yet, we're done for this step (no aggregated result yet).
                if (_frameIndex % Math.Max(1, _measurementService.FramesPerCycle) != 0)
                    return null;

                // 6) On cycle completion, assemble and apply a gradient-based conductivity update and publish a result.
                var mesh = _runtimeContext.RuntimeMesh
                          ?? throw new InvalidOperationException("Runtime mesh missing from reconstruction context.");

                // Snapshot the previous estimate to include it in the result payload.
                var previous = mesh.GetConductivityDistribution();

                // Apply optimizer(s) with regularization to get the updated conductivity.
                var updated = ApplyGradientUpdate(_cycleFrames, previous, stepSize, regularizationWeight);
                if (updated == null)
                    return null;

                // Build a result that captures the discretization and the cycle's frames for UI/export.
                var result = new ReconstructionResult(mesh.GetDiscretization(),
                                                      _runtimeContext.OriginalDistribution,
                                                      previous,
                                                      updated,
                                                      new List<ReconstructionFrame>(_cycleFrames));

                // Persist for UI consumption and clear the buffer for the next cycle.
                Workspace.AddReconstructionResultToWorkspace(result);
                _cycleFrames.Clear();

                // Notify listeners (e.g., view models) of the completed cycle.
                ReconstructionUpdated?.Invoke(this, result);
                return result;
            }
            catch (Exception ex)
            {
                // Log and bubble up: the caller may choose to surface errors to the user.
                _logger.LogError(ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Adds a reconstruction frame to the workspace and optionally notifies listeners for live visualisation.
        /// </summary>
        /// <param name="frame">Frame to surface.</param>
        private void PublishFrame(ReconstructionFrame frame)
        {
            Workspace.AddReconstructionFrameToWorkspace(frame);

            if (VisualizeIterations)
                ReconstructionFrameUpdated?.Invoke(this, frame);
        }

        // Creates an EITMeasurement representing the next frame to process, aligned with electrodes and
        // annotated with the drive-pattern step index. Also ensures the measurement amplitude is consistent
        // with the actual source (real hardware vs. requested excitation).
        private EITMeasurement PrepareMeasurement(double excitationAmplitude)
        {
            if (_runtimeContext == null)
                throw new InvalidOperationException("Reconstruction runtime context is not available.");

            // Ensure at least one frame is available from the measurement source and synchronized with the solver.
            _measurementService.EnsureMeasurements(excitationAmplitude);

            // Get the active electrodes from the runtime mesh for correct mapping of raw measurements to solver order.
            var mesh = _runtimeContext.RuntimeMesh
                       ?? throw new InvalidOperationException("Runtime mesh missing from reconstruction context.");

            var electrodes = mesh.GetElectrodes().Cast<Electrode>().ToList();

            // Pull all prepared measurement frames (source-dependent). If none are available, create a placeholder frame
            // so downstream logic can still run (e.g., simulation-only scenarios).
            var allMeasurements = _measurementService.GetAllMeasurements();
            if (allMeasurements.Count == 0)
            {
                // No frames available: create an empty measurement with the current pattern only.
                return new EITMeasurement(new List<double[]>(), _measurementService.CurrentPattern)
                {   
                    CurrentAmplitude = excitationAmplitude
                };
            }

            // Compute the logical step index within the current cycle for pattern reconstruction and BC mapping.
            int cycleLength = Math.Max(1, _measurementService.FramesPerCycle);
            int stepIndex = _frameIndex % cycleLength;

            // Reorder/prepare a single measurement frame for the solver using mesh electrodes and annotate with step index.
            var preparedFrame = _measurementService.PrepareMeasurementFrame(allMeasurements[stepIndex], electrodes, stepIndex);
            var preparedFrames = new List<double[]> { preparedFrame };
            var stepIndices = new List<int> { stepIndex };

            // Prefer real measurement amplitude if available; otherwise fall back to the requested excitation amplitude.
            var measurement = new EITMeasurement(preparedFrames, _measurementService.CurrentPattern, stepIndices: stepIndices)
            {
                CurrentAmplitude = _measurementService.RealMeasurementAmplitude ?? excitationAmplitude
            };

            return measurement;
        }

        // Applies an optimization update over the accumulated frames of the current cycle.
        // 1) Aggregate optimizer gradients and regularizations across frames.
        // 2) Apply configured numeric optimizer(s) to produce an updated conductivity.
        // 3) Clip the conductivity to physically plausible bounds.
        // 4) Inform persistence and workspace of the new baseline for the next cycle.
        private ConductivityDistribution? ApplyGradientUpdate(IReadOnlyList<ReconstructionFrame> frames,
                                                              ConductivityDistribution currentSigma,
                                                              double stepSize,
                                                              double regularizationWeight)
        {
            if (_runtimeContext == null)
                return null;
            if (frames.Count == 0)
                return null;

            // Combine per-frame optimizer gradients and regularizations into a single averaged gradient per optimizer id.
            var optimizerGradients = AggregateOptimizerGradients(frames, regularizationWeight);

            // Run one optimization step using the configured numeric optimizer(s).
            var updated = ApplyOptimizers(currentSigma, optimizerGradients, stepSize);

            // Keep the estimate in reasonable bounds (implementation-specific policy in the clipper).
            updated = ConductivityClipper.Clip(updated);

            // Synchronize the internal state in persistence and workspace so that subsequent
            // steps compute regularization/gradients with respect to the latest estimate.
            _persistence.UpdateCurrentDistribution(updated);
            Workspace.SetInitialConductivityDistribution(updated);
            return updated;
        }

        // Aggregates optimizer gradients and regularization contributions across the frames of one cycle.
        // The result is a per-optimizer-id ConductivityDistribution representing
        //    (Sum(?J/??) - ? * Sum(?R/??)) / N_frames
        // where ? is the provided regularizationWeight.
        private static Dictionary<string, ConductivityDistribution> AggregateOptimizerGradients(IReadOnlyList<ReconstructionFrame> frames,
                                                                                               double regularizationWeight)
        {
            // Temporary accumulation maps keyed by optimizer id; inner map is elementId -> value.
            var gradientAccum = new Dictionary<string, Dictionary<int, double>>();
            var regAccum = new Dictionary<string, Dictionary<int, double>>();

            foreach (var frame in frames)
            {
                // Sum pure data-misfit gradients per optimizer id
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

                // Sum regularization gradients per optimizer id
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

            // Average over the number of frames to get a stable per-cycle gradient.
            int frameCount = Math.Max(1, frames.Count);
            var result = new Dictionary<string, ConductivityDistribution>();

            // Merge keys from both accumulators since an optimizer id may appear in only one.
            foreach (var optimizerId in gradientAccum.Keys.Union(regAccum.Keys))
            {
                var gradDict = gradientAccum.TryGetValue(optimizerId, out var g) ? g : new Dictionary<int, double>();
                var regDict = regAccum.TryGetValue(optimizerId, out var r) ? r : new Dictionary<int, double>();

                var combined = new Dictionary<int, double>();
                foreach (var kvp in gradDict)
                {
                    double reg = regDict.TryGetValue(kvp.Key, out var regVal) ? regVal : 0.0;
                    // Combine misfit gradient and regularization with ? and average across frames.
                    combined[kvp.Key] = (kvp.Value - regularizationWeight * reg) / frameCount;
                }

                // Include entries that exist only in the regularization map.
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

        // Applies one optimization step with the configured numeric optimizer(s).
        // - If only one optimizer is present, apply it directly to currentSigma using its gradient.
        // - If multiple optimizers exist, compute each candidate update and return the weighted average by configured weights.
        private ConductivityDistribution ApplyOptimizers(ConductivityDistribution currentSigma,
                                                         IReadOnlyDictionary<string, ConductivityDistribution> optimizerGradients,
                                                         double stepSize)
        {
            // If no optimizers are configured, keep the current estimate unchanged.
            if (_runtimeContext == null || _runtimeContext.NumericOptimizers == null || _runtimeContext.NumericOptimizers.Count == 0)
                return currentSigma;

            // Fast-path: single optimizer uses the only available gradient (or an empty one if missing).
            if (_runtimeContext.NumericOptimizers.Count == 1)
            {
                var optimizer = _runtimeContext.NumericOptimizers[0];
                var gradient = optimizerGradients.Values.FirstOrDefault() ?? new ConductivityDistribution(new Dictionary<int, double>());
                var candidate = optimizer.numericOptimizer.OptimizationStep(currentSigma, gradient, stepSize);
                return MergeWithBaseline(currentSigma, candidate);
            }

            // Multiple optimizers case: compute each candidate update and form a weighted average by connection weight.
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

            // Guard: if all weights are zero, fall back to identity update.
            if (totalWeight <= double.Epsilon)
                return currentSigma;

            // Normalize by the total weight to get the final combined candidate.
            var combined = weightedSum.ToDictionary(kvp => kvp.Key, kvp => kvp.Value / totalWeight);
            var combinedDistribution = new ConductivityDistribution(combined);
            return MergeWithBaseline(currentSigma, combinedDistribution);
        }

        // Ensures that an updated conductivity distribution preserves any elements that were not explicitly
        // produced by an optimizer (e.g., sparse updates). Missing keys would otherwise be interpreted as zero
        // conductivity, causing the UI canvas to diverge from the solver state.
        private static ConductivityDistribution MergeWithBaseline(ConductivityDistribution baseline,
                                                                  ConductivityDistribution updated)
        {
            var merged = new Dictionary<int, double>(baseline.Conductivities);
            foreach (var kvp in updated.Conductivities)
                merged[kvp.Key] = kvp.Value;

            return new ConductivityDistribution(merged);
        }
    }
}
