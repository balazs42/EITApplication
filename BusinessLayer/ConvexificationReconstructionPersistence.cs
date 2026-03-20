using DataAccessLayer;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Configurations.ReconstructionConfiguration;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction;
using Utility.Classes.Reconstruction.Convexification;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Exports;

using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace BusinessLayer
{
    /// <summary>
    /// Persistence backend for the convexification reconstruction path.
    /// The implementation keeps the existing FEM mesh and measurement pipeline,
    /// but solves the convexification variables directly on electrode-level data
    /// using a practical residual-minimizing least-squares surrogate of the
    /// chapter's H2 formulation.
    /// </summary>
    public sealed class ConvexificationReconstructionPersistence : ReconstructionPersistenceBase
    {
        private readonly IReconstructionRepository _reconstructionRepository;
        private FEMMesh? _mesh;
        private IDifferentialEquationSolver? _differentialEquationSolver;
        private INumericSolver? _numericSolver;
        private ReconstructionRuntimeContext? _runtimeContext;
        private ConvexificationOptions _options = new();
        private ConductivityDistribution? _originalDistribution;
        private ConductivityDistribution? _currentDistribution;
        private IDrivePatternStrategy _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent);
        private bool _initialized;

        public override bool IsInitialized => _initialized;
        public override IDifferentialEquationSolver? DifferentialEquationSolver => _differentialEquationSolver;

        /// <summary>
        /// Creates the convexification persistence backend with the shared
        /// reconstruction repository used by the rest of the application.
        /// </summary>
        public ConvexificationReconstructionPersistence(IReconstructionRepository reconstructionRepository)
        {
            _reconstructionRepository = reconstructionRepository ?? throw new ArgumentNullException(nameof(reconstructionRepository));
        }

        /// <summary>
        /// Exposes the materialised runtime context so the service layer can
        /// reuse the initial/original conductivity snapshots.
        /// </summary>
        public ReconstructionRuntimeContext? RuntimeContext => _runtimeContext;

        /// <summary>
        /// Initializes the persistence from a prebuilt runtime context.
        /// </summary>
        public void Initialize(ReconstructionRuntimeContext parameters, bool reinit = false)
        {
            if (_initialized && !reinit)
                return;

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            if (reinit)
                ResetState();

            _mesh = parameters.RuntimeMesh
                    ?? Workspace.GetDiscretization() as FEMMesh
                    ?? throw new InvalidOperationException("Convexification reconstruction requires an initialized FEM mesh.");
            _mesh.UpdateElectrodeLengths();

            _numericSolver = parameters.RuntimeNumericSolver
                             ?? NumericSolverFactory.Create(parameters.NumericSolver,
                                                            parameters.UseOmpParallelization,
                                                            parameters.UseCudaAcceleration);
            _differentialEquationSolver = parameters.RuntimeDifferentialEquationSolver
                                          ?? DifferentialEquationSolverFactory.Create(_mesh,
                                                                                      Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FEM,
                                                                                      _numericSolver,
                                                                                      parameters.UseOmpParallelization,
                                                                                      parameters.UseCudaAcceleration);

            _originalDistribution = parameters.OriginalDistribution
                                    ?? Workspace.GetOriginalConductivityDistribution()
                                    ?? Workspace.GetOriginalDiscretization()?.GetConductivityDistribution()
                                    ?? new ConductivityDistribution(_mesh.GetConductivityDistribution().Conductivities);

            var initialDistribution = parameters.InitialDistribution
                                      ?? Workspace.GetInitialConductivityDistribution()
                                      ?? ConductivityDistributionFactory.CreateInitialDistribution(_mesh,
                                                                                                   parameters.InitialDistributionType);
            _currentDistribution = new ConductivityDistribution(initialDistribution.Conductivities);
            _mesh.SetConductivityDistribution(new ConductivityDistribution(initialDistribution.Conductivities));

            ApplyContactImpedanceDefaults(_mesh, parameters);

            parameters.RuntimeMesh = _mesh;
            parameters.RuntimeNumericSolver = _numericSolver;
            parameters.RuntimeDifferentialEquationSolver = _differentialEquationSolver;
            parameters.OriginalDistribution ??= _originalDistribution;
            parameters.InitialDistribution ??= _currentDistribution;

            _runtimeContext = parameters;
            _options = parameters.ConvexificationOptions;
            ConductivityClipper.UpdateBounds(parameters.ConductivityMinimumBound, parameters.ConductivityMaximumBound);
            _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(parameters.DrivePattern, parameters.DrivePatternSkip);
            _initialized = true;
        }

        /// <summary>
        /// Initializes the persistence from the block-configuration runtime
        /// materializer so this path can mirror the existing reconstruction
        /// architecture when a canvas configuration is present.
        /// </summary>
        public void Initialize(CompleteReconstructionConfiguration configuration, bool reinit = false)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var materialized = ReconstructionConfigurationMaterializer.Materialize(configuration);
            Initialize(materialized, reinit);
        }

        /// <summary>
        /// Updates the conductivity estimate stored on the working mesh.
        /// </summary>
        public void UpdateCurrentDistribution(ConductivityDistribution updated)
        {
            if (updated == null)
                throw new ArgumentNullException(nameof(updated));
            if (_mesh == null)
                throw new InvalidOperationException("Convexification mesh is not initialised.");

            _currentDistribution = new ConductivityDistribution(updated.Conductivities);
            _mesh.SetConductivityDistribution(new ConductivityDistribution(updated.Conductivities));
        }

        /// <summary>
        /// Executes a full convexification reconstruction pass on the supplied
        /// measurement cycle and returns the recovered conductivity together with
        /// cycle-wise frame data for the service layer.
        /// </summary>
        public ConvexificationState RunReconstructionCycle(EITMeasurement measurement)
        {
            if (measurement == null)
                throw new ArgumentNullException(nameof(measurement));
            if (!_initialized || _mesh == null || _numericSolver == null)
                throw new InvalidOperationException("Convexification reconstruction is not initialised.");
            if (measurement.Frames.Count == 0)
                throw new InvalidOperationException("Convexification reconstruction requires at least one measurement frame.");

            var warnings = new List<string>();
            var diagnostics = new List<string>();
            var realElectrodes = GetRealElectrodes();
            int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(realElectrodes.Count));
            NormalizeConvexificationOptions(warnings, diagnostics);
            AppendEffectiveParameterDiagnostics(diagnostics, cycleLength);

            var boundaryData = BuildBoundaryData(measurement, realElectrodes, cycleLength, warnings);
            AppendBoundaryDataDiagnostics(boundaryData, diagnostics);
            var laplacian = ConvexificationOperators.BuildCotangentLaplacianMatrix(_mesh);
            var carlemanWeights = ConvexificationOperators.ComputeCarlemanWeights(_mesh, _options.Lambda, _options.Omega);
            var iterationFrames = new List<ReconstructionFrame>();
            var initialDistribution = _currentDistribution != null
                ? new ConductivityDistribution(_currentDistribution.Conductivities)
                : _mesh.GetConductivityDistribution();

            var (rFields, sFields) = CreateInitialFields(boundaryData, realElectrodes, laplacian);
            var (optimizedR,
                 optimizedS,
                 objectiveValue,
                 objectiveHistory,
                 relativeConductivityChange,
                 iterationCount,
                 converged,
                 acceptedDampingHistory) = OptimizeFields(boundaryData,
                                                          realElectrodes,
                                                          laplacian,
                                                          carlemanWeights,
                                                          rFields,
                                                          sFields,
                                                          initialDistribution,
                                                          iterationFrames,
                                                          warnings,
                                                          diagnostics);
            var (wFields,
                 coefficientField,
                 scaleField,
                 rawConductivity,
                 reconstructedConductivity,
                 rawSigmaBelowMinimumFraction,
                 rawSigmaAboveMaximumFraction) = RecoverConductivityEstimate(laplacian,
                                                                             realElectrodes,
                                                                             optimizedR,
                                                                             optimizedS,
                                                                             warnings,
                                                                             diagnostics,
                                                                             "final");
            UpdateCurrentDistribution(reconstructedConductivity);

            if (iterationFrames.Count == 0)
            {
                var bootstrapFrame = BuildIterationFrame(boundaryData,
                                                         realElectrodes,
                                                         optimizedR,
                                                         optimizedS,
                                                         wFields,
                                                         initialDistribution,
                                                         reconstructedConductivity,
                                                         warnings);
                iterationFrames.Add(bootstrapFrame);
                PublishFrame(bootstrapFrame);
                PublishResult(new ReconstructionResult(_mesh.GetDiscretization(),
                                                       _originalDistribution ?? reconstructedConductivity,
                                                       initialDistribution,
                                                       reconstructedConductivity,
                                                       [bootstrapFrame]));
            }

            return new ConvexificationState
            {
                BoundaryData = boundaryData,
                RFields = optimizedR,
                SFields = optimizedS,
                WFields = wFields,
                Frames = iterationFrames,
                ReconstructedConductivity = reconstructedConductivity,
                RecoveredCoefficientField = coefficientField,
                RecoveredScaleField = scaleField,
                ObjectiveValue = objectiveValue,
                IterationCount = iterationCount,
                Converged = converged,
                ObjectiveHistory = objectiveHistory,
                AcceptedDampingHistory = acceptedDampingHistory,
                RelativeConductivityChange = relativeConductivityChange,
                RawRecoveredConductivity = rawConductivity,
                RawSigmaBelowMinimumFraction = rawSigmaBelowMinimumFraction,
                RawSigmaAboveMaximumFraction = rawSigmaAboveMaximumFraction,
                Warnings = warnings,
                Diagnostics = diagnostics
            };
        }

        private void ResetState()
        {
            _mesh = null;
            _differentialEquationSolver = null;
            _numericSolver = null;
            _runtimeContext = null;
            _originalDistribution = null;
            _currentDistribution = null;
            _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent);
            _options = new ConvexificationOptions();
            _initialized = false;
            ResetResults();
        }

        private void NormalizeConvexificationOptions(IList<string> warnings, IList<string> diagnostics)
        {
            if (_runtimeContext == null)
                return;

            ConductivityClipper.UpdateBounds(_runtimeContext.ConductivityMinimumBound, _runtimeContext.ConductivityMaximumBound);
            var (conductivityMinimumBound, _) = ResolveConductivityBounds();
            var (normalizedStepSize, normalizedMinimumStepSize, normalizedDecay) = ConvexificationOperators.NormalizeLineSearchParameters(_options);
            double normalizedMinimumScale = ConvexificationOperators.ResolveMinimumScale(_options, conductivityMinimumBound);

            NormalizeOption(_options.StepSize,
                            normalizedStepSize,
                            "inner step size",
                            value => _options.StepSize = value,
                            warnings,
                            diagnostics);
            NormalizeOption(_options.MinimumStepSize,
                            normalizedMinimumStepSize,
                            "minimum inner step size",
                            value => _options.MinimumStepSize = value,
                            warnings,
                            diagnostics);
            NormalizeOption(_options.LineSearchDecay,
                            normalizedDecay,
                            "line-search decay",
                            value => _options.LineSearchDecay = value,
                            warnings,
                            diagnostics);
            NormalizeOption(_options.ObjectiveAcceptanceTolerance,
                            Math.Max(0.0, double.IsFinite(_options.ObjectiveAcceptanceTolerance) ? _options.ObjectiveAcceptanceTolerance : 1e-6),
                            "objective acceptance tolerance",
                            value => _options.ObjectiveAcceptanceTolerance = value,
                            warnings,
                            diagnostics);
            NormalizeOption(_options.LineSearchRelativeTolerance,
                            Math.Max(0.0, double.IsFinite(_options.LineSearchRelativeTolerance) ? _options.LineSearchRelativeTolerance : 5e-5),
                            "line-search relative tolerance",
                            value => _options.LineSearchRelativeTolerance = value,
                            warnings,
                            diagnostics);
            NormalizeOption(_options.Beta,
                            Math.Max(0.0, double.IsFinite(_options.Beta) ? _options.Beta : 2e-4),
                            "residual regularization beta",
                            value => _options.Beta = value,
                            warnings,
                            diagnostics);
            NormalizeOption(_options.MinimumScale,
                            normalizedMinimumScale,
                            "minimum scale floor",
                            value => _options.MinimumScale = value,
                            warnings,
                            diagnostics);

            if (!double.IsFinite(_options.Lambda) || _options.Lambda <= 0.0)
            {
                warnings.Add($"Invalid convexification lambda {_options.Lambda:G6} was reset to 1.25.");
                _options.Lambda = 1.25;
            }

            if (!double.IsFinite(_options.Epsilon) || Math.Abs(_options.Epsilon) < 1e-8)
            {
                warnings.Add($"Invalid convexification epsilon {_options.Epsilon:G6} was reset to 0.5.");
                _options.Epsilon = 0.5;
            }

            if (_options.MaxIterations < 1)
            {
                warnings.Add($"Invalid convexification inner iteration count {_options.MaxIterations} was reset to 1.");
                _options.MaxIterations = 1;
            }
        }

        private void NormalizeOption(double currentValue,
                                     double normalized,
                                     string name,
                                     Action<double> setter,
                                     IList<string> warnings,
                                     IList<string> diagnostics)
        {
            if (double.IsFinite(currentValue) && Math.Abs(currentValue - normalized) <= 1e-12)
                return;

            warnings.Add($"Convexification {name} was normalized from {currentValue:G6} to {normalized:G6}.");
            setter(normalized);
            AppendDiagnostic(diagnostics, $"Convexification normalized {name} to {normalized:G6}.");
        }

        private void AppendEffectiveParameterDiagnostics(IList<string> diagnostics, int cycleLength)
        {
            var (conductivityMinimumBound, conductivityMaximumBound) = ResolveConductivityBounds();
            double effectiveMinimumScale = ConvexificationOperators.ResolveMinimumScale(_options, conductivityMinimumBound);
            AppendDiagnostic(diagnostics,
                             $"Convexification parameters: cycleLength={cycleLength}, " +
                             $"lambda={_options.Lambda:G6}, epsilon={_options.Epsilon:G6}, " +
                             $"beta={_options.Beta:G6}, innerStep={_options.StepSize:G6}, " +
                             $"minimumInnerStep={_options.MinimumStepSize:G6}, " +
                             $"lineSearchDecay={_options.LineSearchDecay:G6}, " +
                             $"acceptTol={_options.ObjectiveAcceptanceTolerance:G6}, " +
                             $"lineSearchRelativeTol={_options.LineSearchRelativeTolerance:G6}, " +
                             $"residualWeight={_options.InteriorResidualWeight:G6}, " +
                             $"boundaryDirichlet={_options.BoundaryDirichletWeight:G6}, " +
                             $"boundaryNeumann={_options.BoundaryNeumannWeight:G6}, " +
                             $"VResidual={_options.VRecoveryResidualWeight:G6}, " +
                             $"VDirichlet={_options.VRecoveryDirichletWeight:G6}, " +
                             $"VNeumann={_options.VRecoveryNeumannWeight:G6}, " +
                             $"VGradient={_options.VRecoveryGradientWeight:G6}, " +
                             $"VStabilization={_options.VRecoveryStabilizationWeight:G6}, " +
                             $"VMass={_options.VRecoveryMassWeight:G6}, " +
                             $"conductivityBounds=[{conductivityMinimumBound:G6}, {conductivityMaximumBound:G6}], " +
                             $"minimumScale={effectiveMinimumScale:G6}.");
        }

        private void AppendBoundaryDataDiagnostics(IReadOnlyList<ConvexificationBoundaryData> boundaryData, IList<string> diagnostics)
        {
            if (boundaryData.Count == 0)
                return;

            double averageShift = boundaryData.Average(frame => frame.PositivityShift);
            double maximumShift = boundaryData.Max(frame => frame.PositivityShift);
            AppendDiagnostic(diagnostics,
                             $"Convexification boundary proxies: frames={boundaryData.Count}, average positivity shift={averageShift:G6}, maximum positivity shift={maximumShift:G6}.");
        }

        private void AppendIterationDiagnostics(IList<string> diagnostics,
                                                int iteration,
                                                double acceptedDamping,
                                                double objectiveValue,
                                                double relativeObjectiveChange,
                                                double directionNorm,
                                                IReadOnlyList<PotentialDistribution> wFields,
                                                PotentialDistribution coefficientField,
                                                PotentialDistribution scaleField,
                                                ConductivityDistribution rawConductivity,
                                                ConductivityDistribution clippedConductivity,
                                                double rawSigmaBelowMinimumFraction,
                                                double rawSigmaAboveMaximumFraction)
        {
            var wRange = ConvexificationOperators.ComputeFiniteRange(wFields.SelectMany(field => field.Potentials.Values));
            var coefficientRange = ConvexificationOperators.ComputeFiniteRange(coefficientField.Potentials.Values);
            var scaleRange = ConvexificationOperators.ComputeFiniteRange(scaleField.Potentials.Values);
            var rawSigmaRange = ConvexificationOperators.ComputeFiniteRange(rawConductivity.Conductivities.Values);
            double clippedFraction = ComputeClippedFraction(rawConductivity, clippedConductivity);

            AppendDiagnostic(diagnostics,
                             $"Convexification inner {iteration}: objective={objectiveValue:G6}, relativeChange={relativeObjectiveChange:G6}, acceptedDamping={acceptedDamping:G6}, directionNorm={directionNorm:G6}, wRange=[{wRange.Minimum:G6}, {wRange.Maximum:G6}], aRange=[{coefficientRange.Minimum:G6}, {coefficientRange.Maximum:G6}], VRange=[{scaleRange.Minimum:G6}, {scaleRange.Maximum:G6}], rawSigmaRange=[{rawSigmaRange.Minimum:G6}, {rawSigmaRange.Maximum:G6}], rawBelowMin={rawSigmaBelowMinimumFraction:P1}, rawAboveMax={rawSigmaAboveMaximumFraction:P1}, clipped={clippedFraction:P1}.");
        }

        private void AppendRecoveryDiagnostics(IList<string> diagnostics,
                                               string diagnosticLabel,
                                               IReadOnlyList<PotentialDistribution> wFields,
                                               PotentialDistribution coefficientField,
                                               PotentialDistribution scaleField,
                                               (double Minimum, double Maximum, double BelowFraction, double AboveFraction) rawSigmaStats,
                                               double clippedFraction)
        {
            var wRange = ConvexificationOperators.ComputeFiniteRange(wFields.SelectMany(field => field.Potentials.Values));
            var coefficientRange = ConvexificationOperators.ComputeFiniteRange(coefficientField.Potentials.Values);
            var scaleRange = ConvexificationOperators.ComputeFiniteRange(scaleField.Potentials.Values);

            AppendDiagnostic(diagnostics,
                             $"Convexification recovery {diagnosticLabel}: wRange=[{wRange.Minimum:G6}, {wRange.Maximum:G6}], aRange=[{coefficientRange.Minimum:G6}, {coefficientRange.Maximum:G6}], VRange=[{scaleRange.Minimum:G6}, {scaleRange.Maximum:G6}], rawSigmaRange=[{rawSigmaStats.Minimum:G6}, {rawSigmaStats.Maximum:G6}], rawBelowMin={rawSigmaStats.BelowFraction:P1}, rawAboveMax={rawSigmaStats.AboveFraction:P1}, clipped={clippedFraction:P1}.");
        }

        private (double Minimum, double Maximum) ResolveConductivityBounds()
        {
            if (_runtimeContext == null)
                return (ConductivityClipper.MinimumBound, ConductivityClipper.MaximumBound);

            return (_runtimeContext.ConductivityMinimumBound, _runtimeContext.ConductivityMaximumBound);
        }

        private static double ComputeClippedFraction(ConductivityDistribution rawConductivity,
                                                     ConductivityDistribution clippedConductivity)
        {
            int count = rawConductivity.Conductivities.Count;
            if (count == 0)
                return 0.0;

            int changed = 0;
            foreach (var pair in rawConductivity.Conductivities)
            {
                double clipped = clippedConductivity.GetConductivity(pair.Key);
                double tolerance = 1e-10 * Math.Max(1.0, Math.Abs(pair.Value));
                if (Math.Abs(clipped - pair.Value) > tolerance)
                    changed++;
            }

            return changed / (double)count;
        }

        private static bool ShouldWarnOnRawSigmaCollapse((double Minimum, double Maximum, double BelowFraction, double AboveFraction) rawSigmaStats,
                                                         double conductivityMinimumBound)
        {
            if (!double.IsFinite(rawSigmaStats.Minimum) || !double.IsFinite(rawSigmaStats.Maximum))
                return true;

            return rawSigmaStats.BelowFraction > 0.5
                   || rawSigmaStats.Maximum <= conductivityMinimumBound * 1.05;
        }

        private void AppendDiagnostic(IList<string> diagnostics, string message)
        {
            if (!_options.EnableDiagnostics || string.IsNullOrWhiteSpace(message))
                return;

            diagnostics.Add(message);
        }

        private static void ApplyContactImpedanceDefaults(FEMMesh mesh, ReconstructionRuntimeContext parameters)
        {
            foreach (var electrode in mesh.ElectrodesTyped)
            {
                if (!double.IsFinite(electrode.ZContact) || electrode.ZContact < 0.0)
                    electrode.ZContact = parameters.ContactImpedanceOhms;
            }
        }

        private List<FEMElectrode> GetRealElectrodes()
        {
            if (_mesh == null)
                throw new InvalidOperationException("Convexification mesh is not initialised.");

            var electrodes = _mesh.ElectrodesTyped
                .Where(electrode => !electrode.IsVirtual)
                .OrderBy(electrode => electrode.Id)
                .ToList();

            if (electrodes.Count == 0)
                electrodes = _mesh.ElectrodesTyped.OrderBy(electrode => electrode.Id).ToList();

            return electrodes;
        }

        private List<ConvexificationBoundaryData> BuildBoundaryData(EITMeasurement measurement,
                                                                    IReadOnlyList<FEMElectrode> electrodes,
                                                                    int cycleLength,
                                                                    IList<string> warnings)
        {
            var representation = measurement.PatternDescription?.Representation
                                 ?? measurement.Pattern?.Representation
                                 ?? (_runtimeContext?.UsePotentialDifferences == true
                                     ? MeasurementRepresentation.PotentialDifference
                                     : MeasurementRepresentation.Amplitude);

            var measurementSetup = measurement.PatternDescription?.MeasurementSetup
                                   ?? measurement.Pattern?.MeasurementSetup
                                   ?? _runtimeContext?.MeasurementSetup
                                   ?? Workspace.GetElectrodeMeasurementSetup();

            var description = measurement.PatternDescription
                              ?? _drivePatternStrategy.BuildDescription(electrodes.Count,
                                                                         representation,
                                                                         measurementSetup);

            var boundaryData = new List<ConvexificationBoundaryData>(measurement.Frames.Count);
            double excitationAmplitude = measurement.CurrentAmplitude
                                         ?? _runtimeContext?.InitializationCurrentAmplitude
                                         ?? 1.0;

            for (int frameIndex = 0; frameIndex < measurement.Frames.Count; frameIndex++)
            {
                int requestedStep = measurement.StepIndices.Count > frameIndex
                    ? measurement.StepIndices[frameIndex]
                    : frameIndex;
                int normalizedStep = NormalizeStep(requestedStep, cycleLength);
                var step = description.CycleLength > 0 ? description.GetStep(normalizedStep) : null;
                var rawFrame = measurement.Frames[frameIndex];
                var voltages = ExpandElectrodeVoltages(rawFrame,
                                                       step,
                                                       representation,
                                                       electrodes.Count,
                                                       warnings);

                var currents = BuildCurrentVector(electrodes.Count,
                                                  step,
                                                  normalizedStep,
                                                  excitationAmplitude);
                var lengths = electrodes.Select(electrode => ResolveElectrodeLength(electrode)).ToArray();
                var impedances = electrodes.Select(electrode => ResolveContactImpedance(electrode)).ToArray();

                var rawProxy = new double[electrodes.Count];
                for (int electrodeIndex = 0; electrodeIndex < electrodes.Count; electrodeIndex++)
                {
                    rawProxy[electrodeIndex] = voltages[electrodeIndex]
                                               - impedances[electrodeIndex] * currents[electrodeIndex] / lengths[electrodeIndex];
                }

                double rawMin = rawProxy.Length > 0 ? rawProxy.Min() : 0.0;
                double shift = ConvexificationOperators.ComputePositivityShift(rawMin,
                                                                               _options.D0,
                                                                               _options.PositivityMargin);
                if (shift > _options.LargeShiftWarningThreshold)
                    warnings.Add($"Large positivity shift {shift:G4} required at step {requestedStep}.");

                var g0 = new double[electrodes.Count];
                var g1 = new double[electrodes.Count];
                var s0 = new double[electrodes.Count];
                var s1 = new double[electrodes.Count];

                for (int electrodeIndex = 0; electrodeIndex < electrodes.Count; electrodeIndex++)
                {
                    g0[electrodeIndex] = rawProxy[electrodeIndex] + shift;
                    if (!double.IsFinite(g0[electrodeIndex]) || g0[electrodeIndex] <= 0.0)
                    {
                        warnings.Add($"Non-positive g0 encountered at step {requestedStep}, electrode {electrodeIndex}. Applying positivity floor.");
                        g0[electrodeIndex] = Math.Max(_options.D0, _options.PositivityMargin);
                    }

                    g1[electrodeIndex] = currents[electrodeIndex] / lengths[electrodeIndex];
                    s0[electrodeIndex] = Math.Log(g0[electrodeIndex]);
                    s1[electrodeIndex] = g1[electrodeIndex] / g0[electrodeIndex];
                }

                boundaryData.Add(new ConvexificationBoundaryData
                {
                    RequestedStepIndex = requestedStep,
                    NormalizedStepIndex = normalizedStep,
                    PatternStep = step,
                    RawFrame = (double[])rawFrame.Clone(),
                    ElectrodeVoltages = voltages,
                    DriveCurrents = currents,
                    ElectrodeLengths = lengths,
                    ContactImpedances = impedances,
                    PositivityShift = shift,
                    G0 = g0,
                    G1 = g1,
                    S0 = s0,
                    S1 = s1
                });
            }

            var stepIndices = boundaryData.Select(data => data.NormalizedStepIndex).ToList();
            if (stepIndices.Distinct().Count() != stepIndices.Count)
                warnings.Add("Duplicate drive-pattern step indices detected. Derivatives fall back to input ordering for those frames.");

            var s0Derivatives = ConvexificationOperators.ComputeDriveDerivatives(boundaryData.Select(data => data.S0).ToList(),
                                                                                 stepIndices,
                                                                                 cycleLength,
                                                                                 _options.UsePeriodicDriveDerivative,
                                                                                 _options.DerivativeSmoothingWindow,
                                                                                 _options.DerivativeSmoothingPasses,
                                                                                 _options.UsePeriodicDerivativeSmoothing);
            var s1Derivatives = ConvexificationOperators.ComputeDriveDerivatives(boundaryData.Select(data => data.S1).ToList(),
                                                                                 stepIndices,
                                                                                 cycleLength,
                                                                                 _options.UsePeriodicDriveDerivative,
                                                                                 _options.DerivativeSmoothingWindow,
                                                                                 _options.DerivativeSmoothingPasses,
                                                                                 _options.UsePeriodicDerivativeSmoothing);

            for (int frameIndex = 0; frameIndex < boundaryData.Count; frameIndex++)
            {
                boundaryData[frameIndex].B0 = s0Derivatives[frameIndex];
                boundaryData[frameIndex].C0 = s1Derivatives[frameIndex];
                boundaryData[frameIndex].BEpsilon = s0Derivatives[frameIndex]
                    .Select((value, index) => value - _options.Epsilon * boundaryData[frameIndex].S0[index])
                    .ToArray();
                boundaryData[frameIndex].CEpsilon = s1Derivatives[frameIndex]
                    .Select((value, index) => value - _options.Epsilon * boundaryData[frameIndex].S1[index])
                    .ToArray();

                ValidateBoundaryFrame(boundaryData[frameIndex], warnings);
            }

            return boundaryData;
        }

        private (List<PotentialDistribution> RFields, List<PotentialDistribution> SFields) CreateInitialFields(
            IReadOnlyList<ConvexificationBoundaryData> boundaryData,
            IReadOnlyList<FEMElectrode> electrodes,
            Matrix laplacian)
        {
            if (_mesh == null || _numericSolver == null)
                throw new InvalidOperationException("Convexification persistence is not ready.");

            var rFields = new List<PotentialDistribution>(boundaryData.Count);
            var sFields = new List<PotentialDistribution>(boundaryData.Count);

            foreach (var frame in boundaryData)
            {
                var rBoundary = ConvexificationOperators.CreateBoundaryValueMap(_mesh, electrodes, frame.B0);
                var sBoundary = ConvexificationOperators.CreateBoundaryValueMap(_mesh, electrodes, frame.BEpsilon);

                rFields.Add(ConvexificationOperators.SolveDirichletPoisson(_mesh,
                                                                           laplacian,
                                                                           rBoundary,
                                                                           null,
                                                                           _numericSolver,
                                                                           _options.SigmaRecoveryRegularization));
                sFields.Add(ConvexificationOperators.SolveDirichletPoisson(_mesh,
                                                                           laplacian,
                                                                           sBoundary,
                                                                           null,
                                                                           _numericSolver,
                                                                           _options.SigmaRecoveryRegularization));
            }

            return (rFields, sFields);
        }

        private (List<PotentialDistribution> RFields,
                 List<PotentialDistribution> SFields,
                 double ObjectiveValue,
                 IReadOnlyList<double> ObjectiveHistory,
                 double RelativeConductivityChange,
                 int IterationCount,
                 bool Converged,
                 IReadOnlyList<double> AcceptedDampingHistory) OptimizeFields(
            IReadOnlyList<ConvexificationBoundaryData> boundaryData,
            IReadOnlyList<FEMElectrode> electrodes,
            Matrix laplacian,
            IReadOnlyDictionary<int, double> carlemanWeights,
            IReadOnlyList<PotentialDistribution> initialRFields,
            IReadOnlyList<PotentialDistribution> initialSFields,
            ConductivityDistribution initialDistribution,
            IList<ReconstructionFrame> publishedFrames,
            IList<string> warnings,
            IList<string> diagnostics)
        {
            if (_mesh == null || _numericSolver == null)
                throw new InvalidOperationException("Convexification persistence is not ready.");

            var currentR = initialRFields.Select(field => new PotentialDistribution(field.Potentials)).ToList();
            var currentS = initialSFields.Select(field => new PotentialDistribution(field.Potentials)).ToList();
            var previousDistribution = new ConductivityDistribution(initialDistribution.Conductivities);
            var currentObjective = ConvexificationOperators.EvaluateObjective(_mesh,
                                                                              boundaryData,
                                                                              electrodes,
                                                                              currentR,
                                                                              currentS,
                                                                              _options,
                                                                              carlemanWeights);
            double previousObjective = currentObjective.TotalValue;
            var objectiveHistory = new List<double> { previousObjective };
            bool converged = false;
            int completedIterations = 0;
            double lastConductivityChange = double.PositiveInfinity;
            var acceptedDampingHistory = new List<double>();
            var (initialStepSize, minimumStepSize, lineSearchDecay) = ConvexificationOperators.NormalizeLineSearchParameters(_options);

            for (int iteration = 0; iteration < _options.MaxIterations; iteration++)
            {
                completedIterations = iteration + 1;
                var (rDirections, sDirections, directionNorm) = ConvexificationOperators.BuildPreconditionedDescentDirections(_mesh,
                                                                                                                                boundaryData,
                                                                                                                                electrodes,
                                                                                                                                currentR,
                                                                                                                                currentS,
                                                                                                                                currentObjective,
                                                                                                                                carlemanWeights,
                                                                                                                                laplacian,
                                                                                                                                 _numericSolver,
                                                                                                                                 _options);
                if (directionNorm < _options.InnerGradientTolerance)
                {
                    AppendDiagnostic(diagnostics,
                                     $"Convexification inner {iteration + 1}: descent direction norm {directionNorm:G6} fell below tolerance {_options.InnerGradientTolerance:G6}; treating the cycle as stationary.");
                    converged = true;
                    break;
                }

                double damping = initialStepSize;
                double directionScale = 1.0 / Math.Max(1.0, directionNorm);
                List<PotentialDistribution>? acceptedR = null;
                List<PotentialDistribution>? acceptedS = null;
                ConvexificationObjectiveSnapshot? acceptedObjectiveSnapshot = null;
                double acceptedObjective = previousObjective;
                double acceptedDamping = double.NaN;
                double bestCandidateObjective = double.PositiveInfinity;
                double bestCandidateDamping = double.NaN;
                string lastRejectionReason = "no candidate was evaluated";

                while (damping >= minimumStepSize)
                {
                    var blendedR = currentR.Zip(rDirections,
                                                (baseline, increment) => ConvexificationOperators.AddScaledIncrement(baseline, increment, damping * directionScale))
                        .ToList();
                    var blendedS = currentS.Zip(sDirections,
                                                (baseline, increment) => ConvexificationOperators.AddScaledIncrement(baseline, increment, damping * directionScale))
                        .ToList();

                    var snapshot = ConvexificationOperators.EvaluateObjective(_mesh,
                                                                              boundaryData,
                                                                              electrodes,
                                                                              blendedR,
                                                                               blendedS,
                                                                               _options,
                                                                               carlemanWeights);
                    double objective = snapshot.TotalValue;
                    if (double.IsFinite(objective) && objective < bestCandidateObjective)
                    {
                        bestCandidateObjective = objective;
                        bestCandidateDamping = damping;
                    }

                    var (accepted, reason) = ConvexificationOperators.EvaluateObjectiveAcceptance(previousObjective,
                                                                                                   objective,
                                                                                                   _options);
                    AppendDiagnostic(diagnostics,
                                     $"Convexification inner {iteration + 1}, line search: damping={damping:G6}, effectiveStep={damping * directionScale:G6}, previousObjective={previousObjective:G6}, candidateObjective={objective:G6}, accepted={accepted}, reason={reason}.");
                    if (accepted)
                    {
                        acceptedR = blendedR;
                        acceptedS = blendedS;
                        acceptedObjective = objective;
                        acceptedObjectiveSnapshot = snapshot;
                        acceptedDamping = damping;
                        break;
                    }

                    lastRejectionReason = reason;
                    damping *= lineSearchDecay;
                }

                if (acceptedR == null || acceptedS == null)
                {
                    string bestCandidateText = double.IsFinite(bestCandidateObjective)
                        ? $"best candidate objective {bestCandidateObjective:G6} at damping {bestCandidateDamping:G6}"
                        : "no finite candidate objective was found";
                    string message = $"Convexification line search stalled before a stable update was found. Initial damping {initialStepSize:G6}, minimum damping {minimumStepSize:G6}, direction scale {directionScale:G6}, {bestCandidateText}. Last rejection reason: {lastRejectionReason}.";
                    warnings.Add(message);
                    AppendDiagnostic(diagnostics, message);
                    break;
                }

                double relativeChange = Math.Abs(previousObjective - acceptedObjective)
                                        / Math.Max(1.0, Math.Abs(previousObjective));
                acceptedDampingHistory.Add(acceptedDamping);
                currentR = acceptedR;
                currentS = acceptedS;
                previousObjective = acceptedObjective;
                currentObjective = acceptedObjectiveSnapshot ?? currentObjective;
                objectiveHistory.Add(previousObjective);

                var (wFields,
                     coefficientField,
                     scaleField,
                     rawConductivity,
                     reconstructedConductivity,
                     rawSigmaBelowMinimumFraction,
                     rawSigmaAboveMaximumFraction) = RecoverConductivityEstimate(laplacian,
                                                                                 electrodes,
                                                                                 currentR,
                                                                                 currentS,
                                                                                 warnings,
                                                                                 diagnostics,
                                                                                 $"inner {iteration + 1}");
                UpdateCurrentDistribution(reconstructedConductivity);
                lastConductivityChange = ConvexificationOperators.ComputeRelativeConductivityChange(previousDistribution,
                                                                                                    reconstructedConductivity);
                AppendIterationDiagnostics(diagnostics,
                                           iteration + 1,
                                           acceptedDamping,
                                           previousObjective,
                                           relativeChange,
                                           directionNorm,
                                           wFields,
                                           coefficientField,
                                           scaleField,
                                           rawConductivity,
                                           reconstructedConductivity,
                                           rawSigmaBelowMinimumFraction,
                                           rawSigmaAboveMaximumFraction);

                var frame = BuildIterationFrame(boundaryData,
                                                electrodes,
                                                currentR,
                                                currentS,
                                                wFields,
                                                previousDistribution,
                                                reconstructedConductivity,
                                                warnings);
                publishedFrames.Add(frame);
                PublishFrame(frame);
                PublishResult(new ReconstructionResult(_mesh.GetDiscretization(),
                                                       _originalDistribution ?? reconstructedConductivity,
                                                       initialDistribution,
                                                       reconstructedConductivity,
                                                       [frame]));
                previousDistribution = new ConductivityDistribution(reconstructedConductivity.Conductivities);

                if (relativeChange < _options.Tolerance || lastConductivityChange < _options.InnerGradientTolerance)
                {
                    converged = true;
                    break;
                }
            }

            return (currentR,
                    currentS,
                    previousObjective,
                    objectiveHistory,
                    lastConductivityChange,
                    completedIterations,
                    converged,
                    acceptedDampingHistory);
        }

        private (List<PotentialDistribution> WFields,
                 PotentialDistribution CoefficientField,
                 PotentialDistribution ScaleField,
                 ConductivityDistribution RawConductivity,
                 ConductivityDistribution Conductivity,
                 double RawSigmaBelowMinimumFraction,
                 double RawSigmaAboveMaximumFraction) RecoverConductivityEstimate(
            Matrix laplacian,
            IReadOnlyList<FEMElectrode> electrodes,
            IReadOnlyList<PotentialDistribution> rFields,
            IReadOnlyList<PotentialDistribution> sFields,
            IList<string> warnings,
            IList<string> diagnostics,
            string diagnosticLabel)
        {
            if (_mesh == null || _numericSolver == null)
                throw new InvalidOperationException("Convexification conductivity recovery is not initialised.");

            var wFields = rFields
                .Zip(sFields, (rField, sField) => ConvexificationOperators.BuildWField(rField, sField, _options.Epsilon))
                .ToList();

            var rawCoefficientField = ConvexificationOperators.RecoverCoefficientField(_mesh,
                                                                                        wFields,
                                                                                        _options.AverageRecoveredCoefficientAcrossCycle);
            var coefficientField = ConvexificationOperators.SmoothRecoveredCoefficientField(_mesh,
                                                                                            laplacian,
                                                                                            rawCoefficientField,
                                                                                            _numericSolver,
                                                                                            _options.CoefficientSmoothingWeight,
                                                                                            _options.SigmaRecoveryRegularization);

            PotentialDistribution scaleField;
            try
            {
                scaleField = ConvexificationOperators.RecoverScaleField(_mesh,
                                                                        laplacian,
                                                                        coefficientField,
                                                                        electrodes,
                                                                        _numericSolver,
                                                                        _options,
                                                                        ResolveConductivityBounds().Minimum);
            }
            catch (Exception ex)
            {
                warnings.Add($"Scale recovery fallback used: {ex.Message}");
                scaleField = new PotentialDistribution(_mesh.Vertices.ToDictionary(vertex => vertex.GlobalId, _ => 1.0));
            }

            var rawConductivity = ConvexificationOperators.RecoverConductivity(_mesh, scaleField);
            var (conductivityMinimumBound, conductivityMaximumBound) = ResolveConductivityBounds();
            var rawStats = ConvexificationOperators.SummarizeConductivity(rawConductivity,
                                                                          conductivityMinimumBound,
                                                                          conductivityMaximumBound);
            var conductivity = ConductivityClipper.Clip(new ConductivityDistribution(rawConductivity.Conductivities));
            double clippedFraction = ComputeClippedFraction(rawConductivity, conductivity);

            AppendRecoveryDiagnostics(diagnostics,
                                      diagnosticLabel,
                                      wFields,
                                      coefficientField,
                                      scaleField,
                                      rawStats,
                                      clippedFraction);

            if (ShouldWarnOnRawSigmaCollapse(rawStats, conductivityMinimumBound))
            {
                warnings.Add($"Recovered raw conductivity is collapsing during {diagnosticLabel}: min={rawStats.Minimum:G6}, max={rawStats.Maximum:G6}, below-min fraction={rawStats.BelowFraction:P1}.");
            }

            return (wFields,
                    coefficientField,
                    scaleField,
                    rawConductivity,
                    conductivity,
                    rawStats.BelowFraction,
                    rawStats.AboveFraction);
        }

        private ReconstructionFrame BuildIterationFrame(IReadOnlyList<ConvexificationBoundaryData> boundaryData,
                                                        IReadOnlyList<FEMElectrode> electrodes,
                                                        IReadOnlyList<PotentialDistribution> rFields,
                                                        IReadOnlyList<PotentialDistribution> sFields,
                                                        IReadOnlyList<PotentialDistribution> wFields,
                                                        ConductivityDistribution previousDistribution,
                                                        ConductivityDistribution reconstructedConductivity,
                                                        IList<string> warnings)
        {
            if (_mesh == null)
                throw new InvalidOperationException("Convexification mesh is not initialised.");

            var conductivityStep = BuildConductivityStep(previousDistribution, reconstructedConductivity);
            var aggregatedPotential = AveragePotentialFields(wFields);
            var aggregatedAdjoint = AveragePotentialFields(rFields);
            var regularization = BuildResidualMagnitude(boundaryData, rFields, sFields);

            double[] measuredElectrodeValues = boundaryData.Count > 0
                ? (double[])boundaryData[0].ElectrodeVoltages.Clone()
                : Array.Empty<double>();

            double[] simulatedElectrodeValues;
            try
            {
                simulatedElectrodeValues = boundaryData.Count > 0
                    ? SimulateRecoveredVoltages(boundaryData[0], electrodes)
                    : Array.Empty<double>();
            }
            catch (Exception ex)
            {
                warnings.Add($"Forward verification fallback used at step {boundaryData.FirstOrDefault()?.RequestedStepIndex ?? 0}: {ex.Message}");
                simulatedElectrodeValues = measuredElectrodeValues;
            }

            return new ReconstructionFrame(conductivityStep,
                                           aggregatedPotential,
                                           aggregatedAdjoint,
                                           regularization,
                                           measuredElectrodeValues,
                                           simulatedElectrodeValues);
        }

        private PotentialDistribution AveragePotentialFields(IReadOnlyList<PotentialDistribution> fields)
        {
            if (_mesh == null)
                throw new InvalidOperationException("Convexification mesh is not initialised.");

            if (fields.Count == 0)
                return new PotentialDistribution(_mesh.Vertices.ToDictionary(vertex => vertex.GlobalId, _ => 0.0));

            var averaged = new Dictionary<int, double>(fields[0].Potentials.Count);
            foreach (var vertex in _mesh.Vertices)
            {
                double sum = 0.0;
                for (int index = 0; index < fields.Count; index++)
                    sum += fields[index].GetPotential(vertex.GlobalId);

                averaged[vertex.GlobalId] = sum / fields.Count;
            }

            return new PotentialDistribution(averaged);
        }

        private ConductivityDistribution BuildConductivityStep(ConductivityDistribution previousDistribution,
                                                               ConductivityDistribution reconstructedConductivity)
        {
            if (_mesh == null)
                throw new InvalidOperationException("Convexification mesh is not initialised.");

            var delta = new Dictionary<int, double>(_mesh.ElementsTyped.Count);
            foreach (var element in _mesh.ElementsTyped)
            {
                double previousValue = previousDistribution.GetConductivity(element.Id);
                double currentValue = reconstructedConductivity.GetConductivity(element.Id);
                delta[element.Id] = currentValue - previousValue;
            }

            return new ConductivityDistribution(delta);
        }

        private ConductivityDistribution BuildResidualMagnitude(IReadOnlyList<ConvexificationBoundaryData> boundaryData,
                                                                IReadOnlyList<PotentialDistribution> rFields,
                                                                IReadOnlyList<PotentialDistribution> sFields)
        {
            if (_mesh == null)
                throw new InvalidOperationException("Convexification mesh is not initialised.");

            var aggregated = _mesh.ElementsTyped.ToDictionary(element => element.Id, _ => 0.0);
            if (boundaryData.Count == 0)
                return new ConductivityDistribution(aggregated);

            for (int frameIndex = 0; frameIndex < boundaryData.Count; frameIndex++)
            {
                var residuals = ConvexificationOperators.ComputeResiduals(_mesh,
                                                                          rFields[frameIndex],
                                                                          sFields[frameIndex],
                                                                          _options.Epsilon);
                foreach (var element in _mesh.ElementsTyped)
                {
                    double l1 = residuals.L1.TryGetValue(element.Id, out double rValue) ? rValue : 0.0;
                    double l2 = residuals.L2.TryGetValue(element.Id, out double sValue) ? sValue : 0.0;
                    aggregated[element.Id] += Math.Sqrt(l1 * l1 + l2 * l2);
                }
            }

            foreach (var elementId in aggregated.Keys.ToList())
                aggregated[elementId] /= boundaryData.Count;

            return new ConductivityDistribution(aggregated);
        }

        private double[] SimulateRecoveredVoltages(ConvexificationBoundaryData boundaryData,
                                                   IReadOnlyList<FEMElectrode> realElectrodes)
        {
            if (_mesh == null || _differentialEquationSolver == null)
                throw new InvalidOperationException("Convexification forward verification is not initialised.");

            foreach (var electrode in _mesh.ElectrodesTyped)
            {
                electrode.Current = 0.0;
                electrode.IsExcitation = false;
                electrode.IsGround = false;
                electrode.IsMeasuring = true;
                electrode.Potential = 0.0;
            }

            if (boundaryData.PatternStep != null)
            {
                int excitationIndex = boundaryData.PatternStep.Excitation.First;
                int groundIndex = boundaryData.PatternStep.Excitation.Second;
                realElectrodes[excitationIndex].IsExcitation = true;
                realElectrodes[excitationIndex].IsMeasuring = false;
                realElectrodes[excitationIndex].Current = boundaryData.DriveCurrents[excitationIndex];

                realElectrodes[groundIndex].IsGround = true;
                realElectrodes[groundIndex].IsMeasuring = false;
                realElectrodes[groundIndex].Current = boundaryData.DriveCurrents[groundIndex];
            }
            else
            {
                var (excitationIndex, groundIndex) = _drivePatternStrategy.GetElectrodePair(realElectrodes.Count,
                                                                                             boundaryData.NormalizedStepIndex);
                realElectrodes[excitationIndex].IsExcitation = true;
                realElectrodes[excitationIndex].IsMeasuring = false;
                realElectrodes[excitationIndex].Current = boundaryData.DriveCurrents[excitationIndex];
                realElectrodes[groundIndex].IsGround = true;
                realElectrodes[groundIndex].IsMeasuring = false;
                realElectrodes[groundIndex].Current = boundaryData.DriveCurrents[groundIndex];
            }

            var boundaryCondition = new FEMBoundaryCondition(_mesh.ElectrodesTyped.ToList());
            _ = _differentialEquationSolver.Solve(_mesh, boundaryCondition, null);
            return realElectrodes.OrderBy(electrode => electrode.Id)
                .Select(electrode => electrode.Potential)
                .ToArray();
        }

        private double[] ExpandElectrodeVoltages(double[] frame,
                                                 MeasurementPatternStep? step,
                                                 MeasurementRepresentation representation,
                                                 int electrodeCount,
                                                 IList<string> warnings)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            if (representation == MeasurementRepresentation.PotentialDifference)
            {
                var pairs = (step?.MeasurementPairs?.Take(frame.Length).ToList()) ?? Enumerable.Range(0, frame.Length)
                    .Select(index => new ElectrodePair(index % Math.Max(1, electrodeCount),
                                                       (index + 1) % Math.Max(1, electrodeCount)))
                    .ToList();
                return ConvexificationOperators.ReconstructPotentialsFromDifferences(electrodeCount, pairs, frame);
            }

            if (frame.Length == electrodeCount)
                return (double[])frame.Clone();

            var mapped = Enumerable.Repeat(double.NaN, electrodeCount).ToArray();
            if (step?.MeasurementPairs != null && step.MeasurementPairs.Count > 0)
            {
                int usable = Math.Min(frame.Length, step.MeasurementPairs.Count);
                for (int index = 0; index < usable; index++)
                {
                    int electrodeIndex = step.MeasurementPairs[index].First;
                    if (electrodeIndex >= 0 && electrodeIndex < electrodeCount)
                        mapped[electrodeIndex] = frame[index];
                }
            }
            else
            {
                int usable = Math.Min(frame.Length, electrodeCount);
                for (int index = 0; index < usable; index++)
                    mapped[index] = frame[index];
            }

            warnings.Add($"Measurement frame length {frame.Length} did not cover all {electrodeCount} electrodes. Missing electrode amplitudes were interpolated.");
            return ConvexificationOperators.FillMissingElectrodeValues(mapped);
        }

        private double[] BuildCurrentVector(int electrodeCount,
                                            MeasurementPatternStep? step,
                                            int normalizedStep,
                                            double excitationAmplitude)
        {
            var currents = new double[electrodeCount];

            int excitationIndex;
            int groundIndex;
            if (step != null)
            {
                excitationIndex = step.Excitation.First;
                groundIndex = step.Excitation.Second;
            }
            else
            {
                var pair = _drivePatternStrategy.GetElectrodePair(electrodeCount, normalizedStep);
                excitationIndex = pair.Excitation;
                groundIndex = pair.Ground;
            }

            if (excitationIndex >= 0 && excitationIndex < electrodeCount)
                currents[excitationIndex] = excitationAmplitude;
            if (groundIndex >= 0 && groundIndex < electrodeCount)
                currents[groundIndex] = -excitationAmplitude;

            return currents;
        }

        private double ResolveElectrodeLength(FEMElectrode electrode)
        {
            double length = electrode.Length;
            if (!double.IsFinite(length) || length <= _options.ElectrodeLengthFloor)
                length = _options.ElectrodeLengthFloor;
            return length;
        }

        private double ResolveContactImpedance(FEMElectrode electrode)
        {
            if (double.IsFinite(electrode.ZContact) && electrode.ZContact >= 0.0)
                return electrode.ZContact;

            return _runtimeContext?.ContactImpedanceOhms ?? 0.0;
        }

        private static void ValidateBoundaryFrame(ConvexificationBoundaryData frame, IList<string> warnings)
        {
            ValidateArray(frame.G0, nameof(frame.G0), frame.RequestedStepIndex, warnings, requirePositive: true);
            ValidateArray(frame.S0, nameof(frame.S0), frame.RequestedStepIndex, warnings);
            ValidateArray(frame.S1, nameof(frame.S1), frame.RequestedStepIndex, warnings);
            ValidateArray(frame.B0, nameof(frame.B0), frame.RequestedStepIndex, warnings);
            ValidateArray(frame.C0, nameof(frame.C0), frame.RequestedStepIndex, warnings);
            ValidateArray(frame.BEpsilon, nameof(frame.BEpsilon), frame.RequestedStepIndex, warnings);
            ValidateArray(frame.CEpsilon, nameof(frame.CEpsilon), frame.RequestedStepIndex, warnings);
        }

        private static void ValidateArray(IReadOnlyList<double> values,
                                          string name,
                                          int stepIndex,
                                          IList<string> warnings,
                                          bool requirePositive = false)
        {
            for (int index = 0; index < values.Count; index++)
            {
                double value = values[index];
                if (!double.IsFinite(value) || (requirePositive && value <= 0.0))
                {
                    warnings.Add($"Boundary proxy {name} became invalid at step {stepIndex}, electrode {index}; a stable fallback was used.");
                    break;
                }
            }
        }

        private static int NormalizeStep(int stepIndex, int cycleLength)
        {
            if (cycleLength <= 0)
                return stepIndex;

            int normalized = stepIndex % cycleLength;
            return normalized < 0 ? normalized + cycleLength : normalized;
        }

        /// <summary>
        /// Persists convexification results through the shared reconstruction repository.
        /// The stored payload stays compatible with the existing reconstruction browser.
        /// </summary>
        public void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters)
            => _reconstructionRepository.SaveReconstruction(frames, name, parameters);

        /// <summary>
        /// Returns persisted reconstruction metadata for the shared reconstruction browser.
        /// </summary>
        public IEnumerable<ReconstructionInfo> GetReconstructions()
            => _reconstructionRepository.GetReconstructions();

        /// <summary>
        /// Loads a persisted reconstruction using the shared repository format.
        /// </summary>
        public List<ReconstructionResult> LoadReconstruction(string filePath)
            => _reconstructionRepository.LoadReconstruction(filePath);
    }
}
