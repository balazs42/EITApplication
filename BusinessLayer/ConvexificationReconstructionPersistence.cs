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
            var realElectrodes = GetRealElectrodes();
            int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(realElectrodes.Count));

            var boundaryData = BuildBoundaryData(measurement, realElectrodes, cycleLength, warnings);
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
                 converged) = OptimizeFields(boundaryData,
                                             realElectrodes,
                                             laplacian,
                                             carlemanWeights,
                                             rFields,
                                             sFields,
                                             initialDistribution,
                                             iterationFrames,
                                             warnings);
            var (wFields, coefficientField, scaleField, reconstructedConductivity) = RecoverConductivityEstimate(laplacian,
                                                                                                                 realElectrodes,
                                                                                                                 optimizedR,
                                                                                                                 optimizedS,
                                                                                                                 warnings);
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
                RelativeConductivityChange = relativeConductivityChange,
                Warnings = warnings
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
                 bool Converged) OptimizeFields(
            IReadOnlyList<ConvexificationBoundaryData> boundaryData,
            IReadOnlyList<FEMElectrode> electrodes,
            Matrix laplacian,
            IReadOnlyDictionary<int, double> carlemanWeights,
            IReadOnlyList<PotentialDistribution> initialRFields,
            IReadOnlyList<PotentialDistribution> initialSFields,
            ConductivityDistribution initialDistribution,
            IList<ReconstructionFrame> publishedFrames,
            IList<string> warnings)
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
                    converged = true;
                    break;
                }

                double damping = Math.Clamp(_options.StepSize, _options.MinimumStepSize, 1.0);
                List<PotentialDistribution>? acceptedR = null;
                List<PotentialDistribution>? acceptedS = null;
                ConvexificationObjectiveSnapshot? acceptedObjectiveSnapshot = null;
                double acceptedObjective = previousObjective;

                while (damping >= _options.MinimumStepSize)
                {
                    var blendedR = currentR.Zip(rDirections,
                                                (baseline, increment) => ConvexificationOperators.AddScaledIncrement(baseline, increment, damping))
                        .ToList();
                    var blendedS = currentS.Zip(sDirections,
                                                (baseline, increment) => ConvexificationOperators.AddScaledIncrement(baseline, increment, damping))
                        .ToList();

                    var snapshot = ConvexificationOperators.EvaluateObjective(_mesh,
                                                                              boundaryData,
                                                                              electrodes,
                                                                              blendedR,
                                                                              blendedS,
                                                                              _options,
                                                                              carlemanWeights);
                    double objective = snapshot.TotalValue;
                    if (!double.IsFinite(previousObjective) || objective <= previousObjective)
                    {
                        acceptedR = blendedR;
                        acceptedS = blendedS;
                        acceptedObjective = objective;
                        acceptedObjectiveSnapshot = snapshot;
                        break;
                    }

                    damping *= _options.LineSearchDecay;
                }

                if (acceptedR == null || acceptedS == null)
                {
                    warnings.Add("Convexification line search stalled before a stable update was found.");
                    break;
                }

                double relativeChange = Math.Abs(previousObjective - acceptedObjective)
                                        / Math.Max(1.0, Math.Abs(previousObjective));
                currentR = acceptedR;
                currentS = acceptedS;
                previousObjective = acceptedObjective;
                currentObjective = acceptedObjectiveSnapshot ?? currentObjective;
                objectiveHistory.Add(previousObjective);

                var (wFields, _, _, reconstructedConductivity) = RecoverConductivityEstimate(laplacian,
                                                                                             electrodes,
                                                                                             currentR,
                                                                                             currentS,
                                                                                             warnings);
                UpdateCurrentDistribution(reconstructedConductivity);
                lastConductivityChange = ConvexificationOperators.ComputeRelativeConductivityChange(previousDistribution,
                                                                                                    reconstructedConductivity);

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
                    converged);
        }

        private (List<PotentialDistribution> WFields,
                 PotentialDistribution CoefficientField,
                 PotentialDistribution ScaleField,
                 ConductivityDistribution Conductivity) RecoverConductivityEstimate(
            Matrix laplacian,
            IReadOnlyList<FEMElectrode> electrodes,
            IReadOnlyList<PotentialDistribution> rFields,
            IReadOnlyList<PotentialDistribution> sFields,
            IList<string> warnings)
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
                                                                        _options);
            }
            catch (Exception ex)
            {
                warnings.Add($"Scale recovery fallback used: {ex.Message}");
                scaleField = new PotentialDistribution(_mesh.Vertices.ToDictionary(vertex => vertex.GlobalId, _ => 1.0));
            }

            var conductivity = ConductivityClipper.Clip(ConvexificationOperators.RecoverConductivity(_mesh, scaleField));
            return (wFields, coefficientField, scaleField, conductivity);
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
