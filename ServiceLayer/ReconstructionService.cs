using BusinessLayer;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private double _excitationAmplitude;                                // Current amplitude applied to the excitation electrode
        private int _simMeasurementIndex;                                   // Index of the current frame within a cycle
        private List<ReconstructionFrame> _currentCycleFrames = [];         // Frames accumulated within the active cycle
        private ConductivityDistribution? _originalSigma;                   // Ground-truth conductivities (for comparison)
        private ConductivityDistribution? _initialSigma;                    // Starting conductivities for the current iteration/cycle
        private DrivePattern _drivePattern = DrivePattern.Adjecent;         // Selected electrode drive pattern
        private IDrivePatternStrategy _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent); // Drive pattern strategy implementation
        private bool _useOmpParallelization;                                // Exposed from parameters to persistence (not used directly here)
        private bool _usePotentialDifferences;

        /// <summary>
        /// Raised when a full reconstruction result is available (i.e., at the end of a cycle or batch).
        /// </summary>
        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        /// <summary>
        /// Raised for each inverse step, providing the intermediate frame.
        /// </summary>
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        /// <summary>
        /// Creates a new reconstruction service.
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

        /// <summary>
        /// Executes a single forward-solve step with the LBM solver on the current discretization.
        /// Intended for preview/diagnostics. Delegates to the persistence layer.
        /// </summary>
        /// <returns>Predicted potential distribution.</returns>
        /// <summary>
        /// Runs a full inverse solve using the LBM solver until convergence or iteration limit.
        /// </summary>
        /// <param name="maxIterationCount">Maximum number of optimizer steps.</param>
        /// <param name="gradientStepSize">Gradient descent step size.</param>
        /// <param name="regularizationWeight">Regularization weight.</param>
        /// <param name="excitationAmplitude">Current amplitude applied during the solve.</param>
        /// <param name="tolerance">Optional stopping tolerance.</param>
        /// <returns>Final reconstruction result.</returns>
        /// <summary>
        /// Initializes a new reconstruction session for the provided discretization and parameters.
        /// Sets up initial/original conductivity distributions, clears prior state, configures drive
        /// pattern and noise, and initializes the persistence layer.
        /// </summary>
        /// <param name="discretization">Target discretization (FEM/LBM) to reconstruct on.</param>
        /// <param name="parameters">Reconstruction parameters selected by the user.</param>
        /// <param name="reinit">Whether to reinitialize persistence internal caches/state.</param>
        public void InitializeReconstruction(IDiscretization discretization, EITReconstructionParameters parameters, bool reinit)
        {
            try
            {
                Workspace.AddLogMessage("Reconstruction Service", "Reconstruction initialization started with the specified EITReconstructionParameters object.");

                // Store and surface the discretization so downstream UI and components can access it.
                Workspace.SetDiscretization(discretization);
                _discretization = discretization;

                // Reset per-run buffers/counters.
                _simMeasurementIndex = 0;
                _currentCycleFrames.Clear();

                // Clear previous runs from the workspace.
                Workspace.ClearReconstructionFrames();
                Workspace.SetReconstructionResults(new List<ReconstructionResult>());

                // Capture original and initial conductivity distributions. If the workspace doesn't
                // hold them yet, fallback to a deep copy for the original and a factory for the initial.
                _originalSigma = Workspace.GetOriginalDiscretization()?.GetConductivityDistribution()
                                 ?? discretization.DeepCopy().GetConductivityDistribution();
                _initialSigma = Workspace.GetInitialDiscretization()?.GetConductivityDistribution()
                                 ?? ConductivityDistributionFactory.CreateInitialDistribution(discretization, parameters.InitialDistributionType);

                // Apply initial distribution to the active discretization.
                discretization.SetConductivityDistribution(_initialSigma);
                Workspace.SetOriginalConductivityDistribution(_originalSigma);
                Workspace.SetInitialConductivityDistribution(_initialSigma);

                // Bootstrap persistence with the conductivity benchmark values.
                _reconstructionPersistence.SetConductivityDistributions(_originalSigma, _initialSigma);
                _reconstructionPersistence.InitializeReconstruction(discretization, parameters, reinit);

                // Configure drive pattern and potential parallelization flag (consumed in persistence layer).
                _drivePattern = parameters.DrivePattern;
                _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(_drivePattern);
                _useOmpParallelization = parameters.UseOmpParallelization;
                _usePotentialDifferences = parameters.UsePotentialDifferences;

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

                // Initialise the dedicated measurement service so measurement generation is handled centrally.
                _measurementService.Initialize(discretization,
                                               parameters,
                                               _drivePattern,
                                               () => _reconstructionPersistence.GetDifferentialEquationSolver());
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Computes the (excitation, ground) electrode indices for a given step within the drive-pattern cycle.
        /// </summary>
        /// <param name="electrodeCount">Number of electrodes.</param>
        /// <param name="stepIndex">Index of the step within the overall sequence.</param>
        /// <returns>Tuple of excitation and ground indices.</returns>
        private static int GetDriveElectrodeCount(IEnumerable<Electrode> electrodes)
        {
            if (electrodes == null)
                return 0;

            var electrodeList = electrodes as IList<Electrode> ?? electrodes.ToList();
            if (electrodeList.Count == 0)
                return 0;

            int realCount = electrodeList.Count(e => !e.IsVirtual);
            return realCount > 0 ? realCount : electrodeList.Count;
        }

        private (int ExcitationIndex, int GroundIndex) GetDrivePatternPair(int electrodeCount, int stepIndex)
        {
            if (electrodeCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(electrodeCount), "Electrode count must be positive.");

            int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount));
            var (excitation, ground) = _drivePatternStrategy.GetElectrodePair(electrodeCount, stepIndex % cycleLength);
            return (excitation, ground);
        }

        /// <summary>
        /// Applies the current drive-pattern step to the provided electrodes by:
        /// - Resetting all roles and potentials
        /// - Marking one electrode as excitation (positive current) and one as ground (negative current)
        /// - Marking all others as measuring
        /// </summary>
        private void ApplyDrivePatternToElectrodes<TElectrode>(IList<TElectrode> electrodes, double excitationAmplitude, int stepIndex)
            where TElectrode : Electrode
        {
            if (electrodes.Count == 0)
                return;

            // Reset all electrodes to a neutral state for the step.
            foreach (var el in electrodes)
            {
                el.Current = 0.0;
                el.IsExcitation = false;
                el.IsGround = false;
                el.IsMeasuring = true;
                el.Potential = 0.0;
            }

            var realElectrodes = electrodes
                .Select((el, idx) => (Electrode: el, Index: idx))
                .Where(pair => !pair.Electrode.IsVirtual)
                .ToList();

            if (realElectrodes.Count == 0)
                return;

            var (excitationIndex, groundIndex) = GetDrivePatternPair(realElectrodes.Count, stepIndex);

            // Assign excitation electrode and inject current.
            var excitation = realElectrodes[excitationIndex].Electrode;
            excitation.IsExcitation = true;
            excitation.IsMeasuring = false;

            // Always use the requested excitation amplitude without applying any
            // additional offsets so that the solver receives the exact drive pattern.
            excitation.Current = excitationAmplitude;

            // Assign ground electrode and sink the same current.
            var ground = realElectrodes[groundIndex].Electrode;
            ground.IsGround = true;
            ground.IsMeasuring = false;

            ground.Current = -excitationAmplitude;
        }

        /// <summary>
        /// Performs one inverse step against the active discretization by:
        /// - Ensuring measurement frames are available
        /// - Applying the drive-pattern step to electrodes and building the boundary condition
        /// - Mapping the measurement frame to solver order
        /// - Delegating the step to the persistence layer and publishing the frame
        /// - Rolling up results into a <see cref="ReconstructionResult"/> at cycle boundaries
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

                // Recompute electrode roles for this step and build BC.
                double effectiveAmplitude = _measurementService.RealMeasurementAmplitude ?? _excitationAmplitude;
                ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, stepIndex);

                var bc = new FEMBoundaryCondition(electrodes);
                // Expand the raw measurement vector to match the solver ordering (injecting NaNs for excluded electrodes).
                var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList());

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
                double effectiveAmplitude = _measurementService.RealMeasurementAmplitude ?? _excitationAmplitude;
                ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, stepIndex);

                var bc = new LBMBoundaryCondition(electrodes);
                double[] measurement = _measurementService.GetMeasurementForStep(stepIndex);
                var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList());
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
                    await Task.Delay(100, token);
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
            _maxIterationCount = maxIterationCount;
            _stepSize = stepSize;
            _regularizationWeight = regularizationWeight;
            _excitationAmplitude = excitationAmplitude;
            _currentIteration = 0;
            _isPaused = false;
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
        /// </summary>
        public void StopBackgroundReconstruction()
        {
            _cts?.Cancel();
            _backgroundTask = null;
            _isPaused = false;
        }

        /// <summary>
        /// Performs a single reconstruction step on a thread-pool thread and returns the computed frame.
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
                        double effectiveAmplitude = _measurementService.RealMeasurementAmplitude ?? _excitationAmplitude;
                        ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, i);

                        var bc = new FEMBoundaryCondition(electrodes);
                        var measurement = measurements[i];
                        // Convert the measurement snapshot to the solver layout for the current electrode roles.
                        var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList());

                        var frame = _reconstructionPersistence.Step(preparedMeasurement, bc, _stepSize, _regularizationWeight);

                        Workspace.AddReconstructionFrameToWorkspace(frame);
                        _currentCycleFrames.Add(frame);
                        ReconstructionFrameUpdated?.Invoke(this, frame);
                    }

                    // accumulate gradient and update conductivity distribution
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
                        double effectiveAmplitude = _measurementService.RealMeasurementAmplitude ?? _excitationAmplitude;
                        ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, i);
                        var bc = new LBMBoundaryCondition(electrodes);

                        var measurement = measurements[i];
                        var preparedMeasurement = _measurementService.PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList());
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
                        kvp => kvp.Value + (accumGrad.TryGetValue(kvp.Key, out var g) ? g / frameCount : 0.0));
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
        public void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters)
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