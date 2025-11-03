using BusinessLayer;
using System.Diagnostics;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Factories;
using Utility.Classes.VirtualElectrodes;
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
        private List<double[]> _simulatedMeasurements = [];                 // Synthetic/real measurement frames for a cycle
        private int _simMeasurementIndex;                                   // Index of the current frame within a cycle
        private List<ReconstructionFrame> _currentCycleFrames = [];         // Frames accumulated within the active cycle
        private ConductivityDistribution? _originalSigma;                   // Ground-truth conductivities (for comparison)
        private ConductivityDistribution? _initialSigma;                    // Starting conductivities for the current iteration/cycle
        private int _framesPerCycle;                                        // Number of frames in one drive-pattern cycle
        private MeasurementNoiseType _measurementNoiseType = MeasurementNoiseType.None; // Noise model used for simulated/loaded frames
        private double _measurementNoiseAmplitude;                          // Noise amplitude (linear or in dB if negative)
        private DrivePattern _drivePattern = DrivePattern.Adjecent;         // Selected electrode drive pattern
        private IDrivePatternStrategy _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent); // Drive pattern strategy implementation
        private readonly Random _noiseRandom = new();                       // PRNG for noise injection
        private bool _useOmpParallelization;                                // Exposed from parameters to persistence (not used directly here)
        private MeasurementSourceOption _measurementSource = MeasurementSourceOption.Simulated; // Source of frames (real or simulated)
        private double? _realMeasurementAmplitude;                          // Optional amplitude carried with imported measurements

        // Tracks whether the currently loaded measurements include voltages on the driven electrodes
        // ("active") or omit them ("non-active").  This flag is mirrored to the workspace so that
        // the UI can communicate the acquisition mode to the user.
        private ElectrodeMeasurementSetup _measurementSetup = ElectrodeMeasurementSetup.Active;
        private bool _usePotentialDifferences;
        private MeasurementPattern? _measurementPattern;
        private ReconstructionProcessContext? _processContext;

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
        public ReconstructionService(IReconstructionPersistence reconstructionPersistence, ILogger logger)
        {
            _reconstructionPersistence = reconstructionPersistence;
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
                _simulatedMeasurements.Clear();
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

                // Default to active measurement setup (frames include driven electrodes), unless we learn otherwise later.
                _measurementSetup = ElectrodeMeasurementSetup.Active;
                Workspace.SetElectrodeMeasurementSetup(ElectrodeMeasurementSetup.Active);
                _measurementPattern = null;
                Workspace.SetMeasurementPattern(null);

                // Configure drive pattern and potential parallelization flag (consumed in persistence layer).
                _drivePattern = parameters.DrivePattern;
                _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(_drivePattern);
                _useOmpParallelization = parameters.UseOmpParallelization;

                // Determine frames per cycle based on electrode count and pattern.
                var electrodeCount = discretization.GetElectrodes().Count;
                _framesPerCycle = electrodeCount > 0 ? Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount)) : 1;

                // Store noise settings for subsequent simulations/import handling.
                _measurementNoiseType = parameters.MeasurementNoiseType;
                _measurementNoiseAmplitude = parameters.MeasurementNoiseAmplitude;
                _usePotentialDifferences = parameters.UsePotentialDifferences;

                if (_measurementNoiseType == MeasurementNoiseType.None || Math.Abs(_measurementNoiseAmplitude) <= double.Epsilon)
                    Workspace.AddLogMessage("Reconstruction Service", "Measurement noise disabled.");
                else
                {
                    // Negative amplitudes are interpreted as dB; convert to linear for logging clarity.
                    double linearAmplitude = _measurementNoiseAmplitude < 0.0
                        ? Math.Pow(10.0, _measurementNoiseAmplitude / 20.0)
                        : Math.Abs(_measurementNoiseAmplitude);

                    string amplitudeDescriptor = _measurementNoiseAmplitude < 0.0
                        ? string.Format("{0:G4} dB -> {1:G4}", _measurementNoiseAmplitude, linearAmplitude)
                        : linearAmplitude.ToString("G4");

                    Workspace.AddLogMessage("Reconstruction Service",
                        $"Measurement noise enabled: {_measurementNoiseType} (amplitude {amplitudeDescriptor}).");
                }

                Workspace.AddLogMessage("Reconstruction Service",
                    _usePotentialDifferences
                        ? "Using electrode potential differences for reconstruction misfit evaluation."
                        : "Using direct electrode potentials for reconstruction misfit evaluation.");

                // Seed measurement source state; we may swap to real measurements later.
                _measurementSource = Workspace.GetMeasurementSource();
                _realMeasurementAmplitude = null;
                _processContext = ReconstructionProcessContext.Create(parameters, _reconstructionPersistence);
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

            var (excitationIndex, groundIndex) = GetDrivePatternPair(electrodes.Count, stepIndex);

            // Assign excitation electrode and inject current.
            var excitation = electrodes[excitationIndex];
            excitation.IsExcitation = true;
            excitation.IsMeasuring = false;

            // Always use the requested excitation amplitude without applying any
            // additional offsets so that the solver receives the exact drive pattern.
            excitation.Current = excitationAmplitude;

            // Assign ground electrode and sink the same current.
            var ground = electrodes[groundIndex];
            ground.IsGround = true;
            ground.IsMeasuring = false;

            ground.Current = -excitationAmplitude;
        }

        /// <summary>
        /// Synchronizes the local measurement-source mode with the workspace setting. If the source changes,
        /// cached frames are discarded so they can be regenerated from the appropriate origin.
        /// </summary>
        private void SyncMeasurementSource()
        {
            var desired = Workspace.GetMeasurementSource();
            if (desired == _measurementSource)
                return;

            _measurementSource = desired;
            _simulatedMeasurements.Clear();
            _simMeasurementIndex = 0;
            _realMeasurementAmplitude = null;
            _measurementSetup = ElectrodeMeasurementSetup.Active;
            Workspace.SetElectrodeMeasurementSetup(ElectrodeMeasurementSetup.Active);
            _measurementPattern = null;
            Workspace.SetMeasurementPattern(null);
        }

        /// <summary>
        /// Ensures that <see cref="_simulatedMeasurements"/> contains frames for the active discretization.
        /// If real measurements are selected and available, those are adopted; otherwise frames are simulated
        /// on the original discretization (kept immutable) and optional noise is applied.
        /// </summary>
        private void EnsureSimulatedMeasurements()
        {
            if (_simulatedMeasurements.Count > 0)
                return; // Already populated

            int electrodeCount = _discretization switch
            {
                FEMMesh fem => fem.GetElectrodes().Count,
                LBMGrid lbm => lbm.GetElectrodes().Count,
                _ => 0
            };

            // Prefer real measurements if requested and available via the workspace.
            if (_measurementSource == MeasurementSourceOption.Real)
            {
                var measurement = Workspace.GetImportedMeasurement();
                if (measurement != null && measurement.Frames.Count > 0)
                {
                    // Deep clone to avoid accidental modification of the imported buffers.
                    _simulatedMeasurements = measurement.Frames.Select(frame => (double[])frame.Clone()).ToList();
                    _realMeasurementAmplitude = measurement.CurrentAmplitude;

                    AdoptMeasurementMetadata(measurement.Pattern, _simulatedMeasurements, electrodeCount);
                    return;
                }

                Workspace.AddWarningMessage("Real measurement data was requested but is not available. Falling back to simulated measurements.");
                _measurementSource = MeasurementSourceOption.Simulated;
                _realMeasurementAmplitude = null;
            }

            var original = Workspace.GetOriginalDiscretization()
                           ?? throw new NullReferenceException("Original mesh not set.");

            var context = _processContext ?? throw new InvalidOperationException("Reconstruction process is not initialized.");
            if (context.SimulateMeasurements == null)
                throw new InvalidOperationException($"Measurement simulation is not available for {context.Solver} reconstructions.");

            var batch = context.SimulateMeasurements(original, _excitationAmplitude, _drivePattern);

            _simulatedMeasurements = CloneMeasurementsWithNoise(batch.Frames, _measurementNoiseType, _measurementNoiseAmplitude);
            _realMeasurementAmplitude = null;

            AdoptMeasurementMetadata(batch.Pattern, _simulatedMeasurements, electrodeCount, batch.MeasurementSetup);
        }

        /// <summary>
        /// Creates deep copies of the provided frames and applies the requested noise model.
        /// </summary>
        private List<double[]> CloneMeasurementsWithNoise(IEnumerable<double[]> measurements,
            MeasurementNoiseType noiseType,
            double noiseAmplitude)
        {
            var clones = measurements.Select(frame => (double[])frame.Clone()).ToList();
            ApplyMeasurementNoise(clones, noiseType, noiseAmplitude);
            return clones;
        }

        /// <summary>
        /// Applies in-place additive noise to the given frames. If amplitude is negative, it is interpreted
        /// as a value in decibels and converted to a linear scale before sampling.
        /// </summary>
        private void ApplyMeasurementNoise(List<double[]> measurements, MeasurementNoiseType noiseType, double noiseAmplitude)
        {
            if (measurements.Count == 0 || noiseType == MeasurementNoiseType.None)
                return;

            bool amplitudeInDecibels = noiseAmplitude < 0.0;
            double amplitude = amplitudeInDecibels
                ? Math.Pow(10.0, noiseAmplitude / 20.0)
                : Math.Abs(noiseAmplitude);
            if (amplitude <= double.Epsilon)
                return; // Effectively disabled

            foreach (var frame in measurements)
            {
                for (int i = 0; i < frame.Length; i++)
                {
                    // Draw noise from the requested distribution and add to the sample.
                    double noise = noiseType switch
                    {
                        MeasurementNoiseType.Gaussian => NextGaussian(0.0, amplitude),
                        MeasurementNoiseType.Uniform => (_noiseRandom.NextDouble() * 2.0 - 1.0) * amplitude,
                        _ => 0.0
                    };

                    frame[i] += noise;
                }
            }
        }

        /// <summary>
        /// Inspects frame dimensions to infer whether the samples include driven electrodes (active) or
        /// exclude them (non-active). This is mirrored to the workspace for UI and solver decisions.
        /// </summary>
        private void UpdateMeasurementOptionsFromFrames(int electrodeCount, IReadOnlyList<double[]> frames)
        {
            // Guard clauses: fall back to the default "active" interpretation when
            // insufficient context is available.
            if (electrodeCount <= 0 || frames == null || frames.Count == 0)
            {
                _measurementSetup = ElectrodeMeasurementSetup.Active;
                Workspace.SetElectrodeMeasurementSetup(_measurementSetup);
                return;
            }

            int frameLength = frames[0].Length;
            long totalMeasurements = (long)frameLength * frames.Count;
            long expectedActiveTotal = (long)electrodeCount * electrodeCount;
            long expectedNonActiveTotal = (long)electrodeCount * Math.Max(0, electrodeCount - 3);
            int nonActiveAmplitudeLength = Math.Max(0, electrodeCount - 2);
            int nonActiveDifferenceLength = Math.Max(0, electrodeCount - 3);

            bool looksActive = frameLength == electrodeCount || totalMeasurements == expectedActiveTotal;
            bool looksNonActive = !looksActive && (frameLength == nonActiveAmplitudeLength || totalMeasurements == expectedNonActiveTotal);

            _measurementSetup = looksActive ? ElectrodeMeasurementSetup.Active : ElectrodeMeasurementSetup.NonActive;

            if (!looksActive && !looksNonActive)
            {
                Workspace.AddWarningMessage($"Ambiguous measurement frame dimensions (frame length {frameLength}, frame count {frames.Count}, electrodes {electrodeCount}). Assuming {_measurementSetup} electrodes.");
            }

            Workspace.SetElectrodeMeasurementSetup(_measurementSetup);

            bool inferredDifferences = InferPotentialDifferenceMode(electrodeCount, frames);
            ApplyUsePotentialDifferenceSetting(inferredDifferences,
                inferredDifferences
                    ? "Detected potential-difference measurements from frame statistics."
                    : "Detected amplitude measurements from frame statistics.");
        }

        private bool InferPotentialDifferenceMode(int electrodeCount, IReadOnlyList<double[]> frames)
        {
            if (frames == null || frames.Count == 0)
                return _usePotentialDifferences;

            int frameLength = frames[0].Length;
            int nonActiveAmplitudeLength = Math.Max(0, electrodeCount - 2);
            int nonActiveDifferenceLength = Math.Max(0, electrodeCount - 3);

            if (_measurementSetup == ElectrodeMeasurementSetup.NonActive)
            {
                if (frameLength == nonActiveDifferenceLength)
                    return true;
                if (frameLength == nonActiveAmplitudeLength)
                    return false;
            }
            else if (frameLength == nonActiveDifferenceLength)
            {
                // Degenerate case where frames mimic the non-active difference size even though
                // the instrumentation was configured as "active". Treat as difference data.
                return true;
            }

            if (frameLength != electrodeCount)
                return _usePotentialDifferences;

            int framesToInspect = Math.Min(frames.Count, 5);
            int zeroLikeFrames = 0;
            int finiteFrames = 0;

            for (int i = 0; i < framesToInspect; i++)
            {
                var frame = frames[i];
                double sum = 0.0;
                double maxAbs = 0.0;
                int finiteCount = 0;

                foreach (var sample in frame)
                {
                    if (!double.IsFinite(sample))
                        continue;

                    sum += sample;
                    finiteCount++;
                    double abs = Math.Abs(sample);
                    if (abs > maxAbs)
                        maxAbs = abs;
                }

                if (finiteCount == 0)
                    continue;

                finiteFrames++;
                double tolerance = Math.Max(1e-6, maxAbs * 1e-3);
                if (Math.Abs(sum) <= tolerance)
                    zeroLikeFrames++;
            }

            return finiteFrames > 0 && zeroLikeFrames == finiteFrames;
        }

        private void ApplyUsePotentialDifferenceSetting(bool enabled, string reason)
        {
            if (_usePotentialDifferences == enabled)
                return;

            _usePotentialDifferences = enabled;

            var parameters = Workspace.GetReconstructionParameters();
            if (parameters.UsePotentialDifferences != enabled)
                parameters.UsePotentialDifferences = enabled;

            Workspace.AddLogMessage("Reconstruction Service", reason);
            _measurementPattern = null;
            Workspace.SetMeasurementPattern(null);
        }

        private void AdoptMeasurementMetadata(MeasurementPattern? pattern,
                                              IReadOnlyList<double[]> frames,
                                              int electrodeCount,
                                              ElectrodeMeasurementSetup? enforcedSetup = null)
        {
            if (pattern != null)
            {
                if (_measurementSetup != pattern.MeasurementSetup)
                {
                    _measurementSetup = pattern.MeasurementSetup;
                    Workspace.SetElectrodeMeasurementSetup(_measurementSetup);
                }

                bool useDifferences = pattern.Representation == MeasurementRepresentation.PotentialDifference;
                ApplyUsePotentialDifferenceSetting(useDifferences,
                    useDifferences
                        ? "Adopting potential-difference mode from provided measurement pattern."
                        : "Adopting amplitude mode from provided measurement pattern.");

                _measurementPattern = pattern;
                Workspace.SetMeasurementPattern(pattern);
                return;
            }

            if (enforcedSetup.HasValue && _measurementSetup != enforcedSetup.Value)
            {
                _measurementSetup = enforcedSetup.Value;
                Workspace.SetElectrodeMeasurementSetup(_measurementSetup);
            }

            UpdateMeasurementOptionsFromFrames(electrodeCount, frames);
        }

        /// <summary>
        /// Maps a provided measurement frame to the solver's expected per-electrode order. If the input excludes
        /// driven electrodes (non-active mode), NaN placeholders are injected for those positions so that solver
        /// residuals can ignore them. Excess values are truncated with a warning.
        /// </summary>
        private double[] PrepareMeasurementFrame(double[] measurement, IReadOnlyList<Electrode> electrodes)
        {
            if (measurement == null)
                throw new ArgumentNullException(nameof(measurement));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));

            if (electrodes.Count == 0)
                return measurement;

            var virtualSettings = Workspace.GetReconstructionParameters().VirtualElectrodeSettings;
            if (virtualSettings.UseVirtualElectrodes)
            {
                var estimator = VirtualElectrodeEstimatorFactory.Create(virtualSettings);
                var context = BuildForwardContext(electrodes);
                measurement = estimator.CompleteElectrodePotentials(electrodes, measurement, virtualSettings, context);
            }

            // Build the measurement pattern for the current electrode roles so
            // that the sanitiser knows which entries to retain (Options 1 & 4)
            // or which potential differences to form (Options 2 & 3).
            var electrodeProjection = electrodes.ToList();
            var pattern = MeasurementPatternBuilder.Build(electrodeProjection,
                                                          Workspace.GetElectrodeMeasurementSetup(),
                                                          _usePotentialDifferences);
            _measurementPattern = pattern;
            Workspace.SetMeasurementPattern(pattern);

            // MapMeasurement() injects NaNs for channels that should not
            // contribute to the residual (e.g. driven electrodes in non-active
            // modes) so downstream metrics can ignore them naturally.
            return pattern.MapMeasurement(measurement);
        }

        private ForwardModelContext BuildForwardContext(IReadOnlyList<Electrode> electrodes)
        {
            if (_discretization is FEMMesh fem)
            {
                return new ForwardModelContext
                {
                    ElectrodeAngles = fem.GetElectrodeAngles(),
                    RealElectrodeCount = electrodes.Count(e => !e.IsVirtual)
                };
            }

            if (_discretization is LBMGrid lbm)
            {
                return new ForwardModelContext
                {
                    ElectrodeAngles = lbm.GetElectrodeAngles(),
                    RealElectrodeCount = electrodes.Count(e => !e.IsVirtual)
                };
            }

            return new ForwardModelContext
            {
                RealElectrodeCount = electrodes.Count(e => !e.IsVirtual)
            };
        }

        /// <summary>
        /// Samples a normally distributed random value with the provided mean and standard deviation.
        /// </summary>
        private double NextGaussian(double mean, double stdDev)
        {
            // Box–Muller transform
            double u1 = 1.0 - _noiseRandom.NextDouble();
            double u2 = 1.0 - _noiseRandom.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return mean + stdDev * randStdNormal;
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
            SyncMeasurementSource();

            if (_discretization is FEMMesh femMesh)
            {
                EnsureSimulatedMeasurements();

                // Select the measurement frame corresponding to the current
                // excitation pair.  Electrode roles must be reconfigured for
                // each frame so that the boundary condition reflects the
                // rotating drive pattern.
                int electrodeCount = femMesh.GetElectrodes().Count;
                int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount));
                int stepIndex = _simMeasurementIndex % cycleLength;
                var measurement = _simulatedMeasurements[stepIndex];

                // Recompute electrode roles for this step and build BC.
                var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                double effectiveAmplitude = _realMeasurementAmplitude ?? _excitationAmplitude;
                ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, stepIndex);

                var bc = new FEMBoundaryCondition(electrodes);
                // Expand the raw measurement vector to match the solver ordering (injecting NaNs for excluded electrodes).
                var preparedMeasurement = PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList());

                // Delegate one optimization step to the persistence layer.
                var frame = _reconstructionPersistence.Step(preparedMeasurement, bc, _stepSize, _regularizationWeight);

                // Advance frame counters and notify observers.
                _simMeasurementIndex++;
                Workspace.AddReconstructionFrameToWorkspace(frame);
                _currentCycleFrames.Add(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);

                // When the drive-pattern cycle ends, publish a reconstruction result and advance the iteration.
                if (_simMeasurementIndex % _framesPerCycle == 0)
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
                EnsureSimulatedMeasurements();

                // Determine the current drive-pattern step and attach the boundary condition.
                var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
                int electrodeCount = electrodes.Count;
                int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount));
                int stepIndex = _simMeasurementIndex % cycleLength;
                double effectiveAmplitude = _realMeasurementAmplitude ?? _excitationAmplitude;
                ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, stepIndex);

                var bc = new LBMBoundaryCondition(electrodes);
                double[] measurement = _simulatedMeasurements[stepIndex];
                var preparedMeasurement = PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList());
                var frame = _reconstructionPersistence.Step(preparedMeasurement, bc, _stepSize, _regularizationWeight);

                _simMeasurementIndex++;
                Workspace.AddReconstructionFrameToWorkspace(frame);
                _currentCycleFrames.Add(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);

                if (_simMeasurementIndex % _framesPerCycle == 0)
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
                SyncMeasurementSource();

                if (_discretization is FEMMesh femMesh)
                {
                    EnsureSimulatedMeasurements();

                    var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                    int electrodeCount = electrodes.Count;

                    for (int i = 0; i < _simulatedMeasurements.Count; i++)
                    {
                        double effectiveAmplitude = _realMeasurementAmplitude ?? _excitationAmplitude;
                        ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, i);

                        var bc = new FEMBoundaryCondition(electrodes);
                        var measurement = _simulatedMeasurements[i];
                        // Convert the measurement snapshot to the solver layout for the current electrode roles.
                        var preparedMeasurement = PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList());

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
                    EnsureSimulatedMeasurements();

                    for (int i = 0; i < _simulatedMeasurements.Count; i++)
                    {
                        var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
                        double effectiveAmplitude = _realMeasurementAmplitude ?? _excitationAmplitude;
                        ApplyDrivePatternToElectrodes(electrodes, effectiveAmplitude, i);
                        var bc = new LBMBoundaryCondition(electrodes);

                        var measurement = _simulatedMeasurements[i];
                        var preparedMeasurement = PrepareMeasurementFrame(measurement, electrodes.Cast<Electrode>().ToList());
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


        private sealed record ReconstructionProcessContext(
            DifferentialEquationSolver Solver,
            Func<IDiscretization, double, DrivePattern, MeasurementBatch>? SimulateMeasurements)
        {
            public static ReconstructionProcessContext Create(EITReconstructionParameters parameters,
                                                              IReconstructionPersistence persistence) =>
                parameters.DifferentialEquationSolver switch
                {
                    DifferentialEquationSolver.FEM => new ReconstructionProcessContext(
                        DifferentialEquationSolver.FEM,
                        (discretization, amplitude, drivePattern) =>
                        {
                            if (discretization is not FEMMesh mesh)
                                throw new InvalidOperationException("FEM reconstruction requires a FEM mesh.");

                            var frames = persistence.SimulateFemMeasurements(mesh, amplitude, drivePattern);
                            return MeasurementBatch.FromFem(frames);
                        }),
                    DifferentialEquationSolver.LBM => new ReconstructionProcessContext(
                        DifferentialEquationSolver.LBM,
                        (discretization, amplitude, drivePattern) =>
                        {
                            if (discretization is not LBMGrid grid)
                                throw new InvalidOperationException("LBM reconstruction requires an LBM grid.");

                            var measurement = persistence.SimulateLbmMeasurements(grid, amplitude, drivePattern);
                            return MeasurementBatch.FromLbm(measurement);
                        }),
                    DifferentialEquationSolver.Graph => new ReconstructionProcessContext(DifferentialEquationSolver.Graph, null),
                    _ => new ReconstructionProcessContext(parameters.DifferentialEquationSolver, null)
                };
        }

        private sealed record MeasurementBatch(
            List<double[]> Frames,
            double? Amplitude,
            MeasurementPattern? Pattern,
            ElectrodeMeasurementSetup MeasurementSetup)
        {
            public static MeasurementBatch FromFem(List<double[]> frames) =>
                new(frames, null, null, ElectrodeMeasurementSetup.Active);

            public static MeasurementBatch FromLbm(EITMeasurement measurement) =>
                new(measurement.Frames,
                    null,
                    measurement.Pattern,
                    measurement.Pattern?.MeasurementSetup ?? ElectrodeMeasurementSetup.Active);
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