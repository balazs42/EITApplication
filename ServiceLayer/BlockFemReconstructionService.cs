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
            _frameIndex = 0;
            _initialized = true;
        }

        /// <inheritdoc />
        public Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                           double regularizationWeight,
                                                                           double excitationAmplitude)
            => Task.Run(() =>
            {
                return ExecuteReconstructionStep(stepSize, regularizationWeight, excitationAmplitude);
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

                var result = _persistence.RunCycle(measurement, stepSize, regularizationWeight);

                foreach (var frame in result.Frames)
                {
                    PublishFrame(frame);
                }

                _frameIndex += result.Frames.Count;
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
    }
}
