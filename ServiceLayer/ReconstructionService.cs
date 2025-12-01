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
    /// - This service holds only orchestration/state; all numerics are performed by <see cref="IReconstructionPersistence"/>.
    /// - The service is solver-agnostic, switching behavior based on <see cref="IDiscretization"/> runtime type.
    /// - Measurement source (real vs. simulated), measurement representation (amplitude vs. differences),
    ///   and whether driven electrodes are included (active vs. non-active) are inferred and mirrored to the workspace.
    /// </summary>
    public class ReconstructionService : IReconstructionService
    {
        private readonly IReconstructionPersistence _reconstructionPersistence;
        private readonly IMeasurementService _measurementService;
        private readonly ILogger _logger;

        // Background reconstruction state
        private IDiscretization? _discretization;                           // Active discretization (FEM or LBM) used by the solver
        private CancellationTokenSource? _cts;                              // Cancellation for background loop
        private Task? _backgroundTask;                                      // Background reconstruction task
        private bool _isPaused;                                             // Flag to pause the background loop without tearing it down
        private int _maxIterationCount;                                     // Target iteration count for background loop
        private int _currentIteration;                                      // Current iteration index in background reconstruction
        private double _stepSize;                                           // Optimizer step size for inverse steps
        private double _regularizationWeight;                               // Regularization weight sent to the persistence layer
        private double _excitationAmplitude = 10.0;                         // Current amplitude applied to the excitation electrode
        private int _simMeasurementIndex;                                   // Index of the current frame within a cycle
        private List<ReconstructionFrame> _currentCycleFrames = [];         // Frames accumulated within the active cycle
        private ConductivityDistribution? _originalSigma;                   // Ground-truth conductivities (for comparison)
        private ConductivityDistribution? _initialSigma;                    // Starting conductivities for the current iteration/cycle
        private DrivePattern _drivePattern = DrivePattern.Adjecent;         // Selected electrode drive pattern
        private IDrivePatternStrategy _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent); // Drive pattern strategy implementation
        private bool _useOmpParallelization;                                // Exposed from parameters to persistence (not used directly here)
        private bool _usePotentialDifferences;                              // Whether we use V(i) or V(i) - V(i+1) like differences

        /// <summary>
        /// Raised when a full reconstruction result is available (i.e., at the end of a cycle or batch).
        /// </summary>
        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        /// <summary>
        /// Raised for each inverse step, providing the intermediate frame.
        /// </summary>
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        /// <summary>
        /// Construct service with a persistence backend and logger.
        /// </summary>
        /// <param name="reconstructionPersistence">Persistence layer that executes forward/inverse computations.</param>
        /// <param name="logger">Logger used for diagnostics.</param>
        public ReconstructionService(IReconstructionPersistence reconstructionPersistence,
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
        public void InitializeReconstruction(IDiscretization discretization, ReconstructionRuntimeContext parameters, bool reinit)
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

                // 3) Clear workspace history so the UI reflects the new session.
                Workspace.ClearReconstructionFrames();
                Workspace.SetReconstructionResults(new List<ReconstructionResult>());

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
                _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(_drivePattern);
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
                                               () => _reconstructionPersistence.GetDifferentialEquationSolver(),
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
        private ReconstructionFrame? PerformInverseStep()
        {
            // Ensure measurement source (simulated/real) matches the workspace selection.
            _measurementService.SyncMeasurementSource();

            if (_discretization is FEMMesh femMesh)
            {
                _measurementService.EnsureMeasurements(_excitationAmplitude);

                // Select the measurement frame corresponding to the current
                // excitation pair.  Electrode roles must be reconfigured for
                // each frame so that the boundary condition reflects the
                // rotating drive pattern.
                var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                int driveElectrodeCount = GetDriveElectrodeCount(electrodes.Cast<Electrode>());
                int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(driveElectrodeCount));
                int stepIndex = _simMeasurementIndex % cycleLength;
                var measurement = _measurementService.GetMeasurementForStep(stepIndex);

                // Apply electrode roles for this step and build boundary condition
                double effectiveAmplitude = _excitationAmplitude; // Use configured amplitude
                ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, stepIndex, null);
                var bc = new FEMBoundaryCondition(electrodes);

                // Map measurement into solver order
                var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList(), stepIndex);

                // Delegate one optimization step to the persistence layer.
                var frame = _reconstructionPersistence.Step(preparedMeasurement, bc, _stepSize, _regularizationWeight);

                // Advance frame counters and notify observers.
                _simMeasurementIndex++;
                Workspace.AddReconstructionFrameToWorkspace(frame);
                _currentCycleFrames.Add(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);

                // When the drive-pattern cycle ends, publish a reconstruction result and advance the iteration.
                if (_simMeasurementIndex % Math.Max(1, _measurementService.FramesPerCycle) == 0)
                {
                    var result = new ReconstructionResult(_discretization!.GetDiscretization(),
                                                          _originalSigma!,
                                                          _initialSigma!,
                                                          _discretization!.GetConductivityDistribution(),
                                                          [.. _currentCycleFrames]);
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);

                    // Use the freshly reconstructed field as the next cycle's initial state.
                    _initialSigma = result.ReconstructedConductivityDistribution;
                    _reconstructionPersistence.SetConductivityDistributions(_originalSigma!, _initialSigma!);

                    _currentCycleFrames.Clear();
                    _currentIteration++;
                }

                return frame;
            }
            else if (_discretization is LBMGrid lbmGrid)
            {
                _measurementService.EnsureMeasurements(_excitationAmplitude);

                // Determine the current drive-pattern step and attach the boundary condition.
                var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
                int driveElectrodeCount = GetDriveElectrodeCount(electrodes.Cast<Electrode>());
                int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(driveElectrodeCount));
                int stepIndex = _simMeasurementIndex % cycleLength;
                double effectiveAmplitude = _excitationAmplitude;
                var measurement = _measurementService.GetMeasurementForStep(stepIndex);

                ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, stepIndex, null);

                var bc = new LBMBoundaryCondition(electrodes);
                var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList(), stepIndex);
                var frame = _reconstructionPersistence.Step(preparedMeasurement, bc, _stepSize, _regularizationWeight);

                _simMeasurementIndex++;
                Workspace.AddReconstructionFrameToWorkspace(frame);
                _currentCycleFrames.Add(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);

                if (_simMeasurementIndex % Math.Max(1, _measurementService.FramesPerCycle) == 0)
                {
                    var result = new ReconstructionResult(_discretization!.GetDiscretization(),
                                                          _originalSigma!,
                                                          _initialSigma!, 
                                                          _discretization!.GetConductivityDistribution(),
                                                          [.. _currentCycleFrames]);
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
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
        private async Task RunLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _currentIteration < _maxIterationCount)
            {
                if (_isPaused)
                {
                    await Task.Delay(100, token); // Sleep briefly while paused to avoid busy-wait
                    continue;
                }

                PerformInverseStep();
                await Task.Yield(); // Allow UI message pumping between steps
            }
        }

        /// <summary>
        /// Starts a background reconstruction that iterates up to <paramref name="maxIterationCount"/>, 
        /// using the provided optimizer/regularization settings and excitation amplitude.
        /// </summary>
        public void StartBackgroundReconstruction(int maxIterationCount, double stepSize, double regularizationWeight, double excitationAmplitude)
        {
            // Capture run targets and settings for the loop.
            _maxIterationCount = maxIterationCount;
            _stepSize = stepSize;
            _regularizationWeight = regularizationWeight;
            _excitationAmplitude = excitationAmplitude;
            _currentIteration = 0;
            _isPaused = false;

            // Start the loop on a background thread with cancellation support.
            _cts = new CancellationTokenSource();
            _backgroundTask = Task.Run(() => RunLoop(_cts.Token));
        }

        /// <summary>
        /// Pauses the background reconstruction loop.
        /// </summary>
        public void PauseBackgroundReconstruction() => _isPaused = true;

        /// <summary>
        /// Resumes the background reconstruction loop.
        /// </summary>
        public void ResumeBackgroundReconstruction() => _isPaused = false;

        /// <summary>
        /// Stops the background reconstruction loop and resets its task/cancellation state.
        /// Safe to call multiple times.
        /// </summary>
        public void StopBackgroundReconstruction()
        {
            _cts?.Cancel();
            _backgroundTask = null;
            _isPaused = false;
        }

        /// <summary>
        /// Performs a single reconstruction step on a thread-pool thread and returns the computed frame.
        /// Useful for UI commands that want a one-off iteration without background state.
        /// </summary>
        public async Task<ReconstructionFrame?> StepReconstructionAsync()
        {
            var frame = await Task.Run(PerformInverseStep);
            return frame;
        }

        /// <summary>
        /// Executes a full drive-pattern cycle by iterating over all measurement frames. For each step
        /// a boundary condition is built from the current excitation pair and the frame is mapped to the
        /// solver order; after all frames, gradients are accumulated and a conductivity update is applied.
        ///
        /// FEM: maintains step-wise frames and aggregates gradient for a single conductivity update.
        /// LBM: performs similar steps; some grids shift visual excitation markers for display.
        /// </summary>
        /// <param name="stepSize">Gradient step size.</param>
        /// <param name="regularizationWeight">Regularization weight.</param>
        /// <param name="excitationAmplitude">Excitation current amplitude.</param>
        /// <returns>Reconstruction result for the completed cycle, or null if no active discretization.</returns>
        public async Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                               double regularizationWeight,
                                                                               double excitationAmplitude)
        {
            // Record settings for the cycle and reset frame accumulator.
            _stepSize = stepSize;
            _regularizationWeight = regularizationWeight;
            _excitationAmplitude = excitationAmplitude;
            _currentCycleFrames.Clear();

            return await Task.Run(() =>
            {
                // Ensure we are using the correct frame source (real/simulated) and that frames are present.
                _measurementService.SyncMeasurementSource();

                if (_discretization is FEMMesh femMesh)
                {
                    _measurementService.EnsureMeasurements(_excitationAmplitude);

                    var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                    int electrodeCount = electrodes.Count;

                    var measurements = _measurementService.GetAllMeasurements();
                    for (int i = 0; i < measurements.Count; i++)
                    {
                        double effectiveAmplitude = _excitationAmplitude;
                        ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, i, null);

                        var bc = new FEMBoundaryCondition(electrodes);
                        var measurement = measurements[i];
                        // Convert the measurement snapshot to the solver layout for the current electrode roles.
                        var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList(), i);

                        var frame = _reconstructionPersistence.Step(preparedMeasurement, bc, _stepSize, _regularizationWeight);

                        // Persist frame for UI and later aggregation/inspection.
                        Workspace.AddReconstructionFrameToWorkspace(frame);
                        _currentCycleFrames.Add(frame);
                        ReconstructionFrameUpdated?.Invoke(this, frame);
                    }

                    // Accumulate gradients across the cycle and apply a single update step to conductivities.
                    var frameCount = _currentCycleFrames.Count;
                    var prevSigma = _discretization!.GetConductivityDistribution();
                    var accumGrad = new Dictionary<int, double>();
                    foreach (var frame in _currentCycleFrames)
                        foreach (var kvp in frame.ConductivityGradient.Conductivities)
                            accumGrad[kvp.Key] = accumGrad.TryGetValue(kvp.Key, out var g) ? g + kvp.Value : kvp.Value;

                    var newSigmaDict = prevSigma.Conductivities.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value + (accumGrad.TryGetValue(kvp.Key, out var g) ? g / frameCount : 0.0));

                    // Clip conductivity values that would fall below 0.0
                    foreach (var kvp in newSigmaDict)
                        if (kvp.Value < 0.0)
                            newSigmaDict[kvp.Key] = 0.0;

                    var newSigma = new ConductivityDistribution(newSigmaDict);
                    _discretization.SetConductivityDistribution(newSigma);
                    _initialSigma = newSigma; // Next cycle starts from this estimate
                    _reconstructionPersistence.SetConductivityDistributions(_originalSigma!, _initialSigma!);

                    var result = new ReconstructionResult(_discretization!.GetDiscretization(),
                                                          _originalSigma!,
                                                          prevSigma, 
                                                          newSigma, 
                                                          [.. _currentCycleFrames]);
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    _currentIteration++;
                    return result;
                }
                else if (_discretization is LBMGrid lbmGrid)
                {
                    _measurementService.EnsureMeasurements(_excitationAmplitude);

                    var measurements = _measurementService.GetAllMeasurements();
                    for (int i = 0; i < measurements.Count; i++)
                    {
                        var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
                        double effectiveAmplitude = _excitationAmplitude;
                        ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, i, null);
                        var bc = new LBMBoundaryCondition(electrodes);

                        var measurement = measurements[i];
                        var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList(), i);
                        var frame = _reconstructionPersistence.Step(preparedMeasurement, bc, _stepSize, _regularizationWeight);

                        Workspace.AddReconstructionFrameToWorkspace(frame);
                        _currentCycleFrames.Add(frame);
                        ReconstructionFrameUpdated?.Invoke(this, frame);

                        // Some LBM workflows advance excitation markers on the grid for visualization.
                        lbmGrid.ShiftExcitationElectrodes(_drivePattern);
                    }

                    var frameCount = _currentCycleFrames.Count;
                    var prevSigma = _discretization!.GetConductivityDistribution();
                    var accumGrad = new Dictionary<int, double>();
                    foreach (var frame in _currentCycleFrames)
                        foreach (var kvp in frame.ConductivityGradient.Conductivities)
                            accumGrad[kvp.Key] = accumGrad.TryGetValue(kvp.Key, out var g) ? g + kvp.Value : kvp.Value;

                    var newSigmaDict = prevSigma.Conductivities.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value - (accumGrad.TryGetValue(kvp.Key, out var g) ? g / frameCount : 0.0));
                    var newSigma = new ConductivityDistribution(newSigmaDict);
                    _discretization.SetConductivityDistribution(newSigma);
                    _initialSigma = newSigma;
                    _reconstructionPersistence.SetConductivityDistributions(_originalSigma!, _initialSigma!);

                    var result = new ReconstructionResult(_discretization!.GetDiscretization(), 
                                                          _originalSigma!,
                                                          prevSigma, 
                                                          newSigma, 
                                                          [.. _currentCycleFrames]);
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    return result;
                }

                return null;
            });
        }


        // --- Persistence ---
        /// <summary>
        /// Persists a reconstruction to storage using the underlying persistence implementation.
        /// </summary>
        public void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters)
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
        public IEnumerable<ReconstructionInfo> GetReconstructions()
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
        public List<ReconstructionResult> LoadReconstruction(string filePath)
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