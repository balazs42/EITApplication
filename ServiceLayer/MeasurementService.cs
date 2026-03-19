using System;
using System.Collections.Generic;
using System.Linq;
using BusinessLayer;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes.ReconstructionParameters;
using Utility.Logger;

namespace ServiceLayer
{
    /// <summary>
    /// Coordinates acquisition (or simulation) of measurement frames prior to reconstruction.
    /// The service centralises the previously scattered measurement logic so callers only need
    /// to request frames and mapping helpers while it tracks metadata, noise injection and
    /// workspace updates.
    /// </summary>
    public class MeasurementService : IMeasurementService
    {
        private readonly IMeasurementPersistence _measurementPersistence;
        private readonly ILogger _logger;
        private readonly Random _noiseRandom = new();

        private Func<IDifferentialEquationSolver?>? _solverAccessor;
        private IDiscretization? _discretization;
        private DrivePattern _drivePattern = DrivePattern.Adjecent;
        private IDrivePatternStrategy _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent);
        private int _drivePatternSkip;
        private MeasurementNoiseType _noiseType = MeasurementNoiseType.None;
        private double _noiseAmplitude;
        private readonly List<double[]> _measurements = [];
        private MeasurementSourceOption _measurementSource = MeasurementSourceOption.Simulated;
        private double? _realMeasurementAmplitude;
        private ElectrodeMeasurementSetup _measurementSetup = ElectrodeMeasurementSetup.Active;
        private bool _usePotentialDifferences;
        private MeasurementPattern? _measurementPattern;
        private VirtualElectrodeSettings _virtualSettings = new();
        private int _framesPerCycle = 1;
        private ConductivityDistribution? _measurementConductivity;
        private DrivePatternDescription? _patternDescription;

        public MeasurementService(IMeasurementPersistence measurementPersistence, ILogger logger)
        {
            _measurementPersistence = measurementPersistence;
            _logger = logger;
        }

        public int FramesPerCycle => _framesPerCycle;
        public double? RealMeasurementAmplitude => _realMeasurementAmplitude;
        public ElectrodeMeasurementSetup MeasurementSetup => _measurementSetup;
        public MeasurementPattern? CurrentPattern => _measurementPattern;
        public DrivePatternDescription? CurrentPatternDescription => _patternDescription;
        public bool UsePotentialDifferences => _usePotentialDifferences;

        /// <summary>
        /// Configures the measurement service for a new reconstruction session.
        /// Stores the discretization, solver accessor and noise configuration so that
        /// future <see cref="EnsureMeasurements"/> calls can generate or adopt frames.
        /// </summary>
        public void Initialize(IDiscretization discretization,
                               ReconstructionRuntimeContext parameters,
                               DrivePattern drivePattern,
                               Func<IDifferentialEquationSolver?> solverAccessor,
                               ConductivityDistribution? measurementConductivity = null)
        {
            _discretization = discretization ?? throw new ArgumentNullException(nameof(discretization));

            if (_discretization is LBMGrid measLbm)
            {
                measLbm.UpdateGhostConductivityFromNeighbors();
            }

            _drivePattern = drivePattern;
            _drivePatternSkip = Math.Max(0, parameters.DrivePatternSkip);
            _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(drivePattern, _drivePatternSkip);
            _solverAccessor = solverAccessor ?? throw new ArgumentNullException(nameof(solverAccessor));

            _noiseType = parameters.MeasurementNoiseType;
            _noiseAmplitude = parameters.MeasurementNoiseAmplitude;
            _usePotentialDifferences = parameters.UsePotentialDifferences;
            _virtualSettings = parameters.VirtualElectrodeSettings ?? new VirtualElectrodeSettings();
            _measurementConductivity = measurementConductivity;

            _measurementSource = Workspace.GetMeasurementSource();
            _measurementPattern = null;
            _measurements.Clear();
            _realMeasurementAmplitude = null;
            _measurementSetup = Workspace.GetElectrodeMeasurementSetup();
            Workspace.SetElectrodeMeasurementSetup(_measurementSetup);
            Workspace.SetMeasurementPattern(null);
            _patternDescription = null;

            _framesPerCycle = Math.Max(1, DetermineCycleLength());
        }

        /// <summary>
        /// Aligns the cached measurement source with the workspace selection. Switching
        /// from simulated to real data (or vice versa) clears previously generated frames
        /// so new ones can be obtained from the requested origin.
        /// </summary>
        public void SyncMeasurementSource()
        {
            var desired = Workspace.GetMeasurementSource();
            if (desired == _measurementSource)
                return;

            _measurementSource = desired;
            _measurements.Clear();
            _realMeasurementAmplitude = null;
            _measurementPattern = null;
            Workspace.SetMeasurementPattern(null);
            _measurementSetup = Workspace.GetElectrodeMeasurementSetup();
            Workspace.SetElectrodeMeasurementSetup(_measurementSetup);
            _patternDescription = null;
            _framesPerCycle = Math.Max(1, DetermineCycleLength());
        }

        /// <summary>
        /// Guarantees that measurement frames are available. Depending on the configured
        /// source the frames are either cloned from the workspace (real data) or simulated
        /// via the persistence layer using the provided excitation amplitude.
        /// </summary>
        public void EnsureMeasurements(double excitationAmplitude)
        {
            if (_measurements.Count > 0)
                return;
            if (_discretization == null)
                throw new InvalidOperationException("Measurement service has not been initialised.");

            int electrodeCount = DetermineElectrodeCount();
            RefreshPatternDescription(electrodeCount);

            if (_measurementSource == MeasurementSourceOption.Real)
            {
                var measurement = Workspace.GetImportedMeasurement();
                if (measurement != null && measurement.Frames.Count > 0)
                {
                    // Clone the imported buffers so we can inject noise or modifications without
                    // mutating the workspace copy.
                    _measurements.Clear();
                    _measurements.AddRange(measurement.Frames.Select(frame => (double[])frame.Clone()));
                    _realMeasurementAmplitude = measurement.CurrentAmplitude;
                    AdoptMeasurementMetadata(measurement.Pattern, _measurements, electrodeCount, measurement.Pattern?.MeasurementSetup, measurement.PatternDescription);
                    _framesPerCycle = Math.Max(1, _measurements.Count);
                    return;
                }

                // If the user requested real measurements but none are available we fall back
                // to simulated data and inform them via the workspace message stream.
                Workspace.AddWarningMessage("Real measurement data was requested but is not available. Falling back to simulated measurements.");
                _logger.LogWarning("Real measurement data was requested but not available. Falling back to simulated measurements.");
                _measurementSource = MeasurementSourceOption.Simulated;
                _realMeasurementAmplitude = null;
            }

            var solver = _solverAccessor?.Invoke();
            if (solver == null)
                throw new InvalidOperationException("Differential equation solver is not available for measurement simulation.");

            ConductivityDistribution? savedConductivity = null;
            PotentialDistribution? savedPotential = null;
            bool restoreState = false;

            if (_measurementConductivity != null)
            {
                savedConductivity = CloneConductivityDistribution(_discretization.GetConductivityDistribution());
                var currentPotential = _discretization.GetPotentialDistribution();
                savedPotential = currentPotential != null ? ClonePotentialDistribution(currentPotential) : null;
                _discretization.SetConductivityDistribution(CloneConductivityDistribution(_measurementConductivity));
                restoreState = true;
            }

            MeasurementSimulationResult simulation;

            try
            {
                simulation = _discretization switch
                {
                    // Delegate the heavy lifting to the measurement persistence implementation
                    // which mirrors the old reconstruction persistence helpers.
                    FEMMesh fem => _measurementPersistence.SimulateFemMeasurements(fem,
                                                                                   excitationAmplitude,
                                                                                   _drivePattern,
                                                                                   _usePotentialDifferences,
                                                                                   solver,
                                                                                   _measurementSetup,
                                                                                   _virtualSettings,
                                                                                   _drivePatternSkip),
                    LBMGrid lbm => _measurementPersistence.SimulateLbmMeasurements(lbm,
                                                                                   excitationAmplitude,
                                                                                   _drivePattern,
                                                                                   _usePotentialDifferences,
                                                                                   solver,
                                                                                   _measurementSetup,
                                                                                   _virtualSettings,
                                                                                   _drivePatternSkip),
                    _ => throw new InvalidOperationException($"Unsupported discretization type {_discretization.GetType().Name} for measurement simulation.")
                };
            }
            finally
            {
                if (restoreState)
                {
                    if (savedConductivity != null)
                        _discretization.SetConductivityDistribution(savedConductivity);
                    if (savedPotential != null)
                        _discretization.SetPotentialDistribution(savedPotential);
                }
            }

            _measurements.Clear();
            _measurements.AddRange(simulation.Frames.Select(frame => (double[])frame.Clone()));
            _realMeasurementAmplitude = simulation.Amplitude;

            // Noise is injected only for simulated data so real measurements remain pristine.
            ApplyMeasurementNoise(_measurements, _noiseType, _noiseAmplitude);
            // Update measurement setup / pattern inference based on the freshly generated frames.
            AdoptMeasurementMetadata(simulation.Pattern, _measurements, electrodeCount, simulation.MeasurementSetup, simulation.PatternDescription);
            _framesPerCycle = Math.Max(1, _measurements.Count);
        }

        /// <summary>
        /// Returns the measurement frame corresponding to the requested drive-pattern step.
        /// The index is wrapped so callers can iterate indefinitely.
        /// </summary>
        public double[] GetMeasurementForStep(int stepIndex)
        {
            if (_measurements.Count == 0)
                throw new InvalidOperationException("Measurements have not been prepared yet.");

            int index = stepIndex % _measurements.Count;
            if (index < 0)
                index += _measurements.Count;
            return _measurements[index];
        }

        public IReadOnlyList<double[]> GetAllMeasurements() => _measurements;

        /// <summary>
        /// Normalises a raw measurement snapshot to the solver ordering by applying optional
        /// virtual electrode completion and mapping it through the current measurement pattern.
        /// </summary>
        public double[] PrepareMeasurementFrame(double[] measurement, IList<Electrode> electrodes, int stepIndex = 0)
        {
            return BuildStepContext(electrodes, measurement, stepIndex).PreparedFrame;
        }

        /// <summary>
        /// Builds a full context object for the requested drive-pattern step. The context
        /// carries the raw frame, prepared frame, pattern and step description so downstream
        /// services can consistently wire boundary conditions and misfit evaluation.
        /// </summary>
        public MeasurementStepContext BuildStepContext(IList<Electrode> electrodes, double[] frame, int stepIndex)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));

            if (electrodes.Count == 0)
            {
                var emptyPattern = MeasurementPatternBuilder.Build(new List<Electrode>(), ElectrodeMeasurementSetup.Active, false);
                return new MeasurementStepContext(stepIndex, 0, frame, frame, emptyPattern, null, null);
            }

            int driveElectrodeCount = GetDriveElectrodeCount(electrodes);
            RefreshPatternDescription(driveElectrodeCount);

            var description = _patternDescription;
            int cycleLength = description?.CycleLength > 0
                ? description!.CycleLength
                : Math.Max(1, _measurements.Count);
            int normalizedStep = NormalizeStepIndex(stepIndex, cycleLength);

            if (_virtualSettings.ShouldApplyVirtualElectrodes())
            {
                var estimator = VirtualElectrodeEstimatorFactory.Create(_virtualSettings);
                var context = BuildForwardContext(electrodes.ToList());
                frame = estimator.CompleteElectrodePotentials(electrodes.ToList(), frame, _virtualSettings, context);
            }

            MeasurementPattern pattern;
            MeasurementPatternStep? step = null;
            if (description != null)
            {
                step = description.GetStep(normalizedStep);
                pattern = MeasurementPatternBuilder.BuildFromStep(electrodes, step);
            }
            else
            {
                pattern = MeasurementPatternBuilder.Build(electrodes, Workspace.GetElectrodeMeasurementSetup(), _usePotentialDifferences);
            }

            var prepared = pattern.MapMeasurement(frame);
            _measurementPattern = pattern;
            Workspace.SetMeasurementPattern(pattern);

            return new MeasurementStepContext(stepIndex, normalizedStep, frame, prepared, pattern, description, step);
        }

        private int DetermineElectrodeCount()
        {
            return _discretization switch
            {
                FEMMesh fem => GetDriveElectrodeCount(fem.GetElectrodes().Cast<Electrode>()),
                LBMGrid lbm => GetDriveElectrodeCount(lbm.GetElectrodes().Cast<Electrode>()),
                _ => 0
            };
        }

        private int DetermineCycleLength()
        {
            int electrodeCount = DetermineElectrodeCount();
            if (electrodeCount <= 0)
                return 1;
            RefreshPatternDescription(electrodeCount);
            return Math.Max(1, _patternDescription?.CycleLength ?? _drivePatternStrategy.GetCycleLength(electrodeCount));
        }

        private static ConductivityDistribution CloneConductivityDistribution(ConductivityDistribution source)
            => new ConductivityDistribution(source.Conductivities);

        private static PotentialDistribution ClonePotentialDistribution(PotentialDistribution source)
            => new PotentialDistribution(source.Potentials);

        private void RefreshPatternDescription(int electrodeCount)
        {
            if (electrodeCount <= 0)
            {
                _patternDescription = null;
                return;
            }

            var representation = _usePotentialDifferences
                ? MeasurementRepresentation.PotentialDifference
                : MeasurementRepresentation.Amplitude;

            _patternDescription = _drivePatternStrategy.BuildDescription(electrodeCount,
                                                                          representation,
                                                                          _measurementSetup);
        }

        private MeasurementPattern ResolvePatternForStep(IList<Electrode> electrodes, int stepIndex)
        {
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));

            int driveElectrodeCount = GetDriveElectrodeCount(electrodes);
            RefreshPatternDescription(driveElectrodeCount);

            if (_patternDescription == null)
            {
                var fallback = MeasurementPatternBuilder.Build(electrodes,
                                                               Workspace.GetElectrodeMeasurementSetup(),
                                                               _usePotentialDifferences);
                _measurementPattern = fallback;
                Workspace.SetMeasurementPattern(fallback);
                return fallback;
            }

            var step = _patternDescription.GetStep(stepIndex);
            var pattern = MeasurementPatternBuilder.BuildFromStep(electrodes, step);
            _measurementPattern = pattern;
            Workspace.SetMeasurementPattern(pattern);
            return pattern;
        }

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

        private void ApplyMeasurementNoise(List<double[]> measurements, MeasurementNoiseType noiseType, double noiseAmplitude)
        {
            if (measurements.Count == 0 || noiseType == MeasurementNoiseType.None)
                return;
            if (_measurementSource != MeasurementSourceOption.Simulated)
                return;

            bool amplitudeInDecibels = noiseAmplitude < 0.0;
            double amplitude = amplitudeInDecibels
                ? Math.Pow(10.0, noiseAmplitude / 20.0)
                : Math.Abs(noiseAmplitude);
            if (amplitude <= double.Epsilon)
                return;

            foreach (var frame in measurements)
            {
                for (int i = 0; i < frame.Length; i++)
                {
                    // Gaussian uses Box-Muller, uniform simply samples from [-amp, amp].
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

        private double NextGaussian(double mean, double stdDev)
        {
            double u1 = 1.0 - _noiseRandom.NextDouble();
            double u2 = 1.0 - _noiseRandom.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return mean + stdDev * randStdNormal;
        }

        private void AdoptMeasurementMetadata(MeasurementPattern? pattern,
                                              IReadOnlyList<double[]> frames,
                                              int electrodeCount,
                                              ElectrodeMeasurementSetup? enforcedSetup,
                                              DrivePatternDescription? description = null)
        {
            if (description != null)
                _patternDescription = description;

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

        private void UpdateMeasurementOptionsFromFrames(int electrodeCount, IReadOnlyList<double[]> frames)
        {
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
            if (parameters != null && parameters.UsePotentialDifferences != enabled)
                parameters.UsePotentialDifferences = enabled;

            Workspace.AddLogMessage("Measurement Service", reason);
            _measurementPattern = null;
            Workspace.SetMeasurementPattern(null);
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

        private static int NormalizeStepIndex(int stepIndex, int cycleLength)
        {
            if (cycleLength <= 0)
                return 0;

            int normalized = stepIndex % cycleLength;
            return normalized < 0 ? normalized + cycleLength : normalized;
        }
    }
}
