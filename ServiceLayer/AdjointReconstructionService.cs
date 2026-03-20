using BusinessLayer;
using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Factories;
using Utility.Logger;

using Workspace = Utility.Classes.Application.Workspace;
using Utility.Exports;

namespace ServiceLayer
{
    /// <summary>
    /// Coordinates EIT reconstruction workflows and delegates heavy computations
    /// (forward/inverse solves, simulations) to the persistence layer.
    ///
    /// Responsibilities:
    /// - Initialize and maintain reconstruction state for a selected discretization (FEM/LBM)
    /// - Generate/supply measurement frames (real or simulated) and optional noise
    /// - Apply current drive patterns to electrodes and create boundary conditions
    /// - Run background reconstruction loops or single/cycle steps
    /// - Mirror state to the <see cref="Workspace"/> for UI consumption and logging
    /// - Surface progress through <see cref="ReconstructionUpdated"/> and <see cref="ReconstructionFrameUpdated"/>
    ///
    /// Design:
    /// - This service holds only orchestration/state; all numerics are performed by <see cref="IAdjointReconstructionPersistence"/>.
    /// - The service is solver-agnostic, switching behavior based on <see cref="IDiscretization"/> runtime type.
    /// - Measurement source (real vs. simulated), measurement representation (amplitude vs. differences),
    ///   and whether driven electrodes are included (active vs. non-active) are inferred and mirrored to the workspace.
    /// </summary>
    public class AdjointReconstructionService : ReconstructionServiceBase
    {
        private readonly IAdjointReconstructionPersistence _reconstructionPersistence;
        private readonly IMeasurementService _measurementService;
        private readonly ILogger _logger;

        // Background reconstruction state
        private IDiscretization? _discretization;                           // Active discretization (FEM or LBM) used by the solver
        private int _currentIteration;                                      // Current iteration index in background reconstruction
        private int _simMeasurementIndex;                                   // Index of the current frame within a cycle
        private List<ReconstructionFrame> _currentCycleFrames = [];         // Frames accumulated within the active cycle
        private ConductivityDistribution? _originalSigma;                   // Ground-truth conductivities (for comparison)
        private ConductivityDistribution? _initialSigma;                    // Starting conductivities for the current iteration/cycle
        private DrivePattern _drivePattern = DrivePattern.Adjecent;         // Selected electrode drive pattern
        private IDrivePatternStrategy _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent); // Drive pattern strategy implementation
        private int _drivePatternSkip;                                      // Number of skipped electrodes for adjacent/skip-x drive mode
        private bool _useOmpParallelization;                                // Exposed from parameters to persistence (not used directly here)
        private bool _usePotentialDifferences;                              // Whether we use V(i) or V(i) - V(i+1) like differences
        private long _lastLiveFramePublishTick;
        private bool _yieldRequested;

        private const int LiveFramePublishIntervalMs = 50;

        public override bool IsInitialized => _reconstructionPersistence.IsInitialized && _discretization != null;

        /// <summary>
        /// Construct service with a persistence backend and logger.
        /// </summary>
        /// <param name="reconstructionPersistence">Persistence layer that executes forward/inverse computations.</param>
        /// <param name="logger">Logger used for diagnostics.</param>
        public AdjointReconstructionService(IAdjointReconstructionPersistence reconstructionPersistence,
                                            IMeasurementService measurementService,
                                            ILogger logger)
        {
            _reconstructionPersistence = reconstructionPersistence;
            _measurementService = measurementService;
            _logger = logger;
        }

        // NOTE: The next XML comments are intentionally left as historical context. Actual forward/inverse
        // calls are performed via the persistence interface. This service focuses on orchestration.

        /// <summary>
        /// Initializes a new reconstruction session for the provided discretization and parameters.
        /// Sets up initial/original conductivity distributions, clears prior state, configures drive
        /// pattern and noise, and initializes the persistence layer.
        /// </summary>
        /// <param name="discretization">Target discretization (FEM/LBM) to reconstruct on.</param>
        /// <param name="parameters">Reconstruction parameters selected by the user.</param>
        /// <param name="reinit">Whether to reinitialize persistence internal caches/state.</param>
        public override void InitializeReconstruction(IDiscretization discretization, ReconstructionRuntimeContext parameters, bool reinit)
        {
            try
            {
                Workspace.AddLogMessage("Reconstruction Service", "Reconstruction initialization started with the specified runtime context.");

                // 1) Surface discretization (mesh/grid) globally and cache locally for quick access.
                Workspace.SetDiscretization(discretization);
                _discretization = discretization;

                // 2) Reset per-run counters/collections.
                _simMeasurementIndex = 0;
                _currentCycleFrames.Clear();
                _lastLiveFramePublishTick = 0;
                _yieldRequested = false;

                // 3) Clear workspace history so the UI reflects the new session.
                Workspace.ClearReconstructionFrames();
                Workspace.SetReconstructionResults(new List<ReconstructionResult>());
                ClearPublishedResults();
                _reconstructionPersistence.ResetResults();

                // 4) Prepare conductivity references:
                //    - _originalSigma: ground-truth for comparison/metrics (kept immutable)
                //    - _initialSigma: starting point for iterative updates
                _originalSigma = Workspace.GetOriginalDiscretization()?.GetConductivityDistribution()
                                 ?? discretization.DeepCopy().GetConductivityDistribution();

                // LBM measurement simulations must run on the same lattice instance that will
                // later be used for reconstruction to prevent the ghost layer/boundary topology
                // from being rebuilt. FEM meshes can be safely cloned.
                bool shareMeasurementGrid = discretization is LBMGrid;
                IDiscretization measurementDiscretization = shareMeasurementGrid
                    ? discretization
                    : discretization.DeepCopy();

                if (!shareMeasurementGrid && _originalSigma != null)
                {
                    measurementDiscretization.SetConductivityDistribution(_originalSigma);
                }

                _initialSigma = Workspace.GetInitialDiscretization()?.GetConductivityDistribution()
                                 ?? ConductivityDistributionFactory.CreateInitialDistribution(discretization, parameters.InitialDistributionType);

                // 5) Apply the initial distribution to the active discretization so the solver starts from it.
                discretization.SetConductivityDistribution(_initialSigma);
                Workspace.SetOriginalConductivityDistribution(_originalSigma);
                Workspace.SetInitialConductivityDistribution(_initialSigma);

                // 6) Bootstrap persistence with the current conductivity references and initialize backend.
                _reconstructionPersistence.SetConductivityDistributions(_originalSigma, _initialSigma);
                _reconstructionPersistence.InitializeReconstruction(discretization, parameters, reinit);

                // 7) Configure drive pattern strategy and parallelization flag (used down in persistence).
                _drivePattern = parameters.DrivePattern;
                _drivePatternSkip = Math.Max(0, parameters.DrivePatternSkip);
                _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(_drivePattern, _drivePatternSkip);
                _useOmpParallelization = parameters.UseOmpParallelization;
                _usePotentialDifferences = parameters.UsePotentialDifferences;

                // Log noise configuration for traceability (delegating actual noise handling to the measurement service).
                if (parameters.MeasurementNoiseType == MeasurementNoiseType.None || Math.Abs(parameters.MeasurementNoiseAmplitude) <= double.Epsilon)
                    Workspace.AddLogMessage("Reconstruction Service", "Measurement noise disabled.");
                else
                {
                    // Negative amplitudes are interpreted as dB; convert to linear for logging clarity.
                    double linearAmplitude = parameters.MeasurementNoiseAmplitude < 0.0
                        ? Math.Pow(10.0, parameters.MeasurementNoiseAmplitude / 20.0)
                        : Math.Abs(parameters.MeasurementNoiseAmplitude);

                    string amplitudeDescriptor = parameters.MeasurementNoiseAmplitude < 0.0
                        ? string.Format("{0:G4} dB -> {1:G4}", parameters.MeasurementNoiseAmplitude, linearAmplitude)
                        : linearAmplitude.ToString("G4");

                    Workspace.AddLogMessage("Reconstruction Service",
                        $"Measurement noise enabled: {parameters.MeasurementNoiseType} (amplitude {amplitudeDescriptor}).");
                }

                Workspace.AddLogMessage("Reconstruction Service",
                    _usePotentialDifferences
                        ? "Using electrode potential differences for reconstruction misfit evaluation."
                        : "Using direct electrode potentials for reconstruction misfit evaluation.");

                // 8) Initialise the dedicated measurement service so measurement generation is handled centrally.
                _measurementService.Initialize(measurementDiscretization,
                                               parameters,
                                               _drivePattern,
                                               () => _reconstructionPersistence.DifferentialEquationSolver,
                                               shareMeasurementGrid ? _originalSigma : null);
            }
            catch(Exception ex)
            {
                // Bubble the exception after logging; caller is expected to handle/report.
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Counts the number of "driving-capable" electrodes, i.e., the number of real (non-virtual)
        /// electrodes if present; otherwise fall back to the total electrode count.
        /// </summary>
        private static int GetDriveElectrodeCount(IEnumerable<Electrode> electrodes)
        {
            if (electrodes == null)
                return 0; // Defensive: no electrodes known

            var electrodeList = electrodes as IList<Electrode> ?? electrodes.ToList();
            if (electrodeList.Count == 0)
                return 0; // None present

            // Prefer the number of real electrodes; if all are virtual, use the total.
            int realCount = electrodeList.Count(e => !e.IsVirtual);
            return realCount > 0 ? realCount : electrodeList.Count;
        }

        /// <summary>
        /// Return the driving electrode pair (excitation, ground) for a given step within the pattern cycle.
        /// The strategy object encapsulates the pattern logic (adjacent, opposite, etc.).
        /// </summary>
        private (int ExcitationIndex, int GroundIndex) GetDrivePatternPair(int electrodeCount, int stepIndex)
        {
            if (electrodeCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(electrodeCount), "Electrode count must be positive.");

            // Normalise the step index to the cycle length so callers can pass monotonically increasing indices.
            int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount));
            var (excitation, ground) = _drivePatternStrategy.GetElectrodePair(electrodeCount, stepIndex % cycleLength);
            return (excitation, ground);
        }

        /// <summary>
        /// Applies the current drive-pattern step to the provided electrodes by:
        /// - Resetting all roles and potentials
        /// - Marking one electrode as excitation (positive current) and one as ground (negative current)
        /// - Marking all others as measuring
        ///
        /// The solver expects exactly one source and one sink per step; this method enforces that contract.
        /// </summary>
        private void ApplyDrivePatternToElectrodes<TElectrode>(IList<TElectrode> electrodes, double excitationAmplitude, int stepIndex, MeasurementPatternStep? patternStep)
            where TElectrode : Electrode
        {
            if (electrodes.Count == 0)
                return; // Nothing to do

            // Reset all electrodes to a neutral state for the step.
            foreach (var el in electrodes)
            {
                el.Current = 0.0;
                el.IsExcitation = false;
                el.IsGround = false;
                el.IsMeasuring = true;
                el.Potential = 0.0; // Potential will be solved; ensure no stale values leak
            }

            // Only real electrodes should be selected for driving; virtual contacts serve measurement completion only.
            var realElectrodes = electrodes
                .Select((el, idx) => (Electrode: el, Index: idx))
                .Where(pair => !pair.Electrode.IsVirtual)
                .ToList();

            if (realElectrodes.Count == 0)
                return; // Edge case: no real electrodes configured

            var (excitationIndex, groundIndex) = patternStep != null
                ? (patternStep.Excitation.First, patternStep.Excitation.Second)
                : GetDrivePatternPair(realElectrodes.Count, stepIndex);

            // Assign excitation electrode and inject current.
            var excitation = realElectrodes[excitationIndex].Electrode;
            excitation.IsExcitation = true;
            excitation.IsMeasuring = false; // A driven electrode is not a measurement channel in this step

            // Always use the requested excitation amplitude without applying any
            // additional offsets so that the solver receives the exact drive pattern.
            excitation.Current = excitationAmplitude;

            // Assign ground electrode and sink the same current.
            var ground = realElectrodes[groundIndex].Electrode;
            ground.IsGround = true;
            ground.IsMeasuring = false;
            ground.Current = -excitationAmplitude; // KCL: net injected current sums to zero
        }

        /// <summary>
        /// Performs one inverse step against the active discretization by:
        /// - Ensuring measurement frames are available
        /// - Applying the drive-pattern step to electrodes and building the boundary condition
        /// - Mapping the measurement frame to solver order
        /// - Delegating the step to the persistence layer and publishing the frame
        /// - Rolling up results into a <see cref="ReconstructionResult"/> at cycle boundaries
        ///
        /// Returns null when no active discretization is available.
        /// </summary>
        private ReconstructionFrame? PerformInverseStep(double stepSize,
                                                        double regularizationWeight,
                                                        double excitationAmplitude,
                                                        bool forceFrameNotification = false,
                                                        Action<ReconstructionResult>? onResultProduced = null)
        {
            // Ensure measurement source (simulated/real) matches the workspace selection.
            _measurementService.SyncMeasurementSource();

            // Keep all frames across all iterations so the reconstruction page can play back
            // the full run, not only the most recent drive-pattern cycle.

            if (_discretization is FEMMesh femMesh)
            {
                _measurementService.EnsureMeasurements(excitationAmplitude);

                // Select the measurement frame corresponding to the current
                // excitation pair.  Electrode roles must be reconfigured for
                // each frame so that the boundary condition reflects the
                // rotating drive pattern.
                var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                var electrodeProjection = electrodes.Cast<Electrode>().ToList();
                int driveElectrodeCount = GetDriveElectrodeCount(electrodeProjection);
                int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(driveElectrodeCount));
                int stepIndex = _simMeasurementIndex % cycleLength;
                var measurement = _measurementService.GetMeasurementForStep(stepIndex);

                // Apply electrode roles for this step and build boundary condition
                double effectiveAmplitude = excitationAmplitude; // Use configured amplitude
                ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, stepIndex, null);
                var bc = new FEMBoundaryCondition(electrodes);

                // Map measurement into solver order
                var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodeProjection, stepIndex);

                // Delegate one optimization step to the persistence layer.
                var frame = CreateHistoryFrame(_reconstructionPersistence.Step(preparedMeasurement, bc, stepSize, regularizationWeight));

                // Advance frame counters and notify observers.
                _simMeasurementIndex++;
                _currentCycleFrames.Add(frame);
                PublishFrameToWorkspace(frame, forceFrameNotification);

                // When the drive-pattern cycle ends, publish a reconstruction result and advance the iteration.
                if (_simMeasurementIndex % Math.Max(1, _measurementService.FramesPerCycle) == 0)
                {
                    var reconstructedSigma = _discretization!.GetConductivityDistribution();
                    var result = new ReconstructionResult(_discretization!.GetDiscretization(),
                                                          _originalSigma!,
                                                          _initialSigma!.CreateCompactHistoryClone(),
                                                          reconstructedSigma.CreateCompactHistoryClone(),
                                                          [.. _currentCycleFrames]);
                    PublishResultToWorkspace(result);
                    onResultProduced?.Invoke(result);

                    // Use the freshly reconstructed field as the next cycle's initial state.
                    _initialSigma = reconstructedSigma;
                    _reconstructionPersistence.SetConductivityDistributions(_originalSigma!, _initialSigma!);

                    _currentCycleFrames.Clear();
                    _currentIteration++;
                }

                return frame;
            }
            else if (_discretization is LBMGrid lbmGrid)
            {
                _measurementService.EnsureMeasurements(excitationAmplitude);

                // Determine the current drive-pattern step and attach the boundary condition.
                var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
                var electrodeProjection = electrodes.Cast<Electrode>().ToList();
                int driveElectrodeCount = GetDriveElectrodeCount(electrodeProjection);
                int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(driveElectrodeCount));
                int stepIndex = _simMeasurementIndex % cycleLength;
                double effectiveAmplitude = excitationAmplitude;
                var measurement = _measurementService.GetMeasurementForStep(stepIndex);

                ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, stepIndex, null);

                var bc = new LBMBoundaryCondition(electrodes);
                var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodeProjection, stepIndex);
                var frame = CreateHistoryFrame(_reconstructionPersistence.Step(preparedMeasurement, bc, stepSize, regularizationWeight));

                _simMeasurementIndex++;
                _currentCycleFrames.Add(frame);
                PublishFrameToWorkspace(frame, forceFrameNotification);

                if (_simMeasurementIndex % Math.Max(1, _measurementService.FramesPerCycle) == 0)
                {
                    var reconstructedSigma = _discretization!.GetConductivityDistribution();
                    var result = new ReconstructionResult(_discretization!.GetDiscretization(),
                                                          _originalSigma!,
                                                          _initialSigma!.CreateCompactHistoryClone(), 
                                                          reconstructedSigma.CreateCompactHistoryClone(),
                                                          [.. _currentCycleFrames]);
                    PublishResultToWorkspace(result);
                    onResultProduced?.Invoke(result);
                    _currentCycleFrames.Clear();
                    _currentIteration++;
                }

                return frame;
            }

            return null; // No active discretization
        }

        /// <summary>
        /// Background reconstruction loop honoring pause/cancel requests and iteration limits.
        /// Performs one inverse step per iteration and yields to keep the UI responsive.
        /// </summary>
        protected override async Task RunCoreAsync(int maxIterationCount,
                                                   double stepSize,
                                                   double regularizationWeight,
                                                   double excitationAmplitude,
                                                   CancellationToken cancellationToken)
        {
            _currentIteration = 0;
            _yieldRequested = false;

            while (!cancellationToken.IsCancellationRequested && _currentIteration < maxIterationCount)
            {
                await WaitWhilePausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    break;

                PerformInverseStep(stepSize, regularizationWeight, excitationAmplitude);
                if (VisualizeIterations && _yieldRequested)
                {
                    _yieldRequested = false;
                    await Task.Yield();
                }
            }
        }

        protected override async Task<ReconstructionResult?> StepCoreAsync(double stepSize,
                                                                           double regularizationWeight,
                                                                           double excitationAmplitude)
        {
            return await Task.Run(() =>
            {
                ReconstructionResult? result = null;
                PerformInverseStep(stepSize, regularizationWeight, excitationAmplitude, true, produced => result = produced);
                _yieldRequested = false;
                return result;
            });
        }

        /// <summary>
        /// Executes one complete drive-pattern cycle by repeatedly invoking the same per-frame
        /// inverse step used by the interactive/background reconstruction flow.
        ///
        /// This keeps the "run full cycle" path numerically identical to the standard adjoint
        /// pipeline: the configured optimizer, step size, regularization weight, clipping policy,
        /// and per-frame conductivity updates are all respected.
        /// </summary>
        /// <param name="stepSize">Gradient step size.</param>
        /// <param name="regularizationWeight">Regularization weight.</param>
        /// <param name="excitationAmplitude">Excitation current amplitude.</param>
        /// <returns>Reconstruction result for the completed cycle, or null if no active discretization.</returns>
        public override async Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                                           double regularizationWeight,
                                                                                           double excitationAmplitude)
        {
            _currentCycleFrames.Clear();
            _yieldRequested = false;

            return await Task.Run(() =>
            {
                if (_discretization == null)
                    return null;

                // Ensure we are using the correct frame source (real/simulated) and that frames are present.
                _measurementService.SyncMeasurementSource();
                _measurementService.EnsureMeasurements(excitationAmplitude);

                ReconstructionResult? result = null;
                int cycleLength = Math.Max(1, _measurementService.FramesPerCycle);
                for (int i = 0; i < cycleLength; i++)
                {
                    PerformInverseStep(stepSize,
                                       regularizationWeight,
                                       excitationAmplitude,
                                       true,
                                       produced => result = produced);
                }

                _yieldRequested = false;
                return result;
            });
        }

        /// <summary>
        /// Adds a reconstruction frame to the workspace and optionally notifies listeners for live visualisation.
        /// </summary>
        /// <param name="frame">Frame to surface.</param>
        private void PublishFrameToWorkspace(ReconstructionFrame frame, bool forceNotify = false)
        {
            if (!VisualizeIterations)
                return;

            Workspace.AddReconstructionFrameToWorkspace(frame);

            if (!forceNotify && !ShouldPublishLiveFrame())
                return;

            _yieldRequested = true;
            base.PublishFrame(frame);
        }

        /// <summary>
        /// Adds a completed reconstruction result to the workspace and notifies subscribers.
        /// </summary>
        /// <param name="result">Result to surface.</param>
        private void PublishResultToWorkspace(ReconstructionResult result)
        {
            Workspace.AddReconstructionResultToWorkspace(result);
            _yieldRequested = true;
            base.PublishResult(result);
        }

        private bool ShouldPublishLiveFrame()
        {
            long now = Environment.TickCount64;
            long elapsed = unchecked(now - _lastLiveFramePublishTick);
            if (elapsed >= LiveFramePublishIntervalMs || elapsed < 0)
            {
                _lastLiveFramePublishTick = now;
                return true;
            }

            return false;
        }

        private static ReconstructionFrame CreateHistoryFrame(ReconstructionFrame frame)
        {
            return new ReconstructionFrame(frame.ConductivityGradient.CreateCompactHistoryClone(),
                                           frame.CalculatedPotentialDistribution.CreateCompactHistoryClone(),
                                           frame.CalculatedAdjointDistribution.CreateCompactHistoryClone(),
                                           new ConductivityDistribution([]),
                                           frame.MeasuredElectrodeValues,
                                           frame.SimulatedElectrodeValues);
        }


        // --- Persistence ---
        /// <summary>
        /// Persists a reconstruction to storage using the underlying persistence implementation.
        /// </summary>
        public override void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters)
        {
            try
            {
                _reconstructionPersistence.SaveReconstruction(frames, name, parameters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Enumerates available reconstructions from storage.
        /// </summary>
        public override IEnumerable<ReconstructionInfo> GetReconstructions()
        {
            try
            {
                return _reconstructionPersistence.GetReconstructions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Loads a reconstruction from storage and mirrors frames/results to the workspace for UI access.
        /// Sets the initial conductivity distribution to the one from the first result for consistency.
        /// </summary>
        public override List<ReconstructionResult> LoadReconstruction(string filePath)
        {
            try
            {
                var frames = _reconstructionPersistence.LoadReconstruction(filePath);
                Workspace.SetReconstructionResults(frames);
                Workspace.SetReconstructionFrames([.. frames.SelectMany(r => r.Frames)]);
                Workspace.SetInitialConductivityDistribution(frames.FirstOrDefault()?.InitialConductivitiyDistribution);
                return frames;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }
    }
}

