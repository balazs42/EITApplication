using System.Collections.Generic;
using System.Numerics;
using Utility.Classes;
using Utility.Classes.Configurations.ReconstructionConfiguration;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers;
using Utility.Classes.Solvers.FiniteElementSolver;

namespace BusinessLayer
{
    public class BlockFemReconstructionPersistence
    {
        private FEMMesh? _mesh = null;
        private IDifferentialEquationSolver? _differentialEquationSolver = null;
        private INumericSolver? _numericSolver = null;
        private List<(string id, double connectionWeight, IRegularizer regulizer)>? _regularizers = null;
        private List<(string id, double connectionWeight, IErrorMetric errorMetric)>? _errorMetrics = null;
        private List<(string id, double connectionWeight, INumericOptimizer numericOptimizer)>? _numericOptimizers = null;

        private CompleteReconstructionConfiguration? _completeReconstructionConfiguration = null;

        /// <summary>
        /// Materialized runtime context derived from the block configuration. Contains
        /// the mesh, solver instances and weighted reconstruction components.
        /// </summary>
        public ReconstructionRuntimeContext? RuntimeContext { get; private set; }

        private InitialDistributionTypes _initialDistributionType = InitialDistributionTypes.Homogeneous;
        private ConductivityDistribution _originalDistribution;
        private ConductivityDistribution _initialDistribution;

        private ElectrodeMeasurementSetup _measurementSetup = ElectrodeMeasurementSetup.Active;
        private bool _usePotentialDifferences = false;

        private IReadOnlyList<WeightedConnectionSnapshot>? _connections;
        private Dictionary<string, (double weight, IRegularizer regulizer)> _regularizerMap = new();
        private Dictionary<string, (double weight, IErrorMetric errorMetric)> _errorMetricMap = new();
        private Dictionary<string, (double weight, INumericOptimizer optimizer)> _optimizerMap = new();

        private bool _initialized = false; 

        /// <summary>
        /// Exposes the active differential equation solver so services can share it with
        /// measurement preparation pipelines.
        /// </summary>
        public IDifferentialEquationSolver? DifferentialEquationSolver => _differentialEquationSolver;

        public void Initialize(CompleteReconstructionConfiguration configuration)
        {
            if(_initialized)
                return;

            _completeReconstructionConfiguration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            RuntimeContext = ReconstructionConfigurationMaterializer.Materialize(configuration);

            _mesh = RuntimeContext.RuntimeMesh;
            _differentialEquationSolver = RuntimeContext.RuntimeDifferentialEquationSolver;
            _numericSolver = RuntimeContext.RuntimeNumericSolver;
            _regularizers = RuntimeContext.Regularizers;
            _errorMetrics = RuntimeContext.ErrorMetrics;
            _numericOptimizers = RuntimeContext.NumericOptimizers;
            _initialDistributionType = RuntimeContext.InitialDistributionType;
            _originalDistribution = RuntimeContext.OriginalDistribution ?? throw new InvalidOperationException("Original distribution missing from runtime context.");
            _initialDistribution = RuntimeContext.InitialDistribution ?? throw new InvalidOperationException("Initial distribution missing from runtime context.");
            _measurementSetup = RuntimeContext.MeasurementSetup;
            _usePotentialDifferences = RuntimeContext.UsePotentialDifferences;
            _connections = RuntimeContext.AllConnections;

            BuildLookupMaps();

            _initialized = true;
        }

        /// <summary>
        /// Updates the internally tracked conductivity distribution to keep regularization
        /// and gradient calculations aligned with the latest optimization step.
        /// </summary>
        /// <param name="updated">Most recent conductivity estimate.</param>
        public void UpdateCurrentDistribution(ConductivityDistribution updated)
        {
            _initialDistribution = updated ?? throw new ArgumentNullException(nameof(updated));
            _mesh?.SetConductivityDistribution(updated);
        }

        /// <summary>
        /// Prepares fast lookup dictionaries from block ids to their runtime instances
        /// so that later gradient assembly can follow the explicit canvas wiring.
        /// </summary>
        private void BuildLookupMaps()
        {
            _regularizerMap = new Dictionary<string, (double weight, IRegularizer regulizer)>();
            _errorMetricMap = new Dictionary<string, (double weight, IErrorMetric errorMetric)>();
            _optimizerMap = new Dictionary<string, (double weight, INumericOptimizer optimizer)>();

            if (_completeReconstructionConfiguration == null)
                return;

            var regularizerBlocks = _completeReconstructionConfiguration.Blocks.Where(b => b.Type == BlockType.Regularizer).ToList();
            var errorBlocks = _completeReconstructionConfiguration.Blocks.Where(b => b.Type == BlockType.ErrorMetric).ToList();
            var optimizerBlocks = _completeReconstructionConfiguration.Blocks.Where(b => b.Type == BlockType.Optimizer).ToList();

            if (_regularizers != null)
            {
                for (int i = 0; i < Math.Min(_regularizers.Count, regularizerBlocks.Count); i++)
                {
                    var blockId = regularizerBlocks[i].Id;
                    var entry = _regularizers[i];
                    _regularizerMap[blockId] = (entry.connectionWeight, entry.regulizer);
                }
            }

            if (_errorMetrics != null)
            {
                for (int i = 0; i < Math.Min(_errorMetrics.Count, errorBlocks.Count); i++)
                {
                    var blockId = errorBlocks[i].Id;
                    var entry = _errorMetrics[i];
                    _errorMetricMap[blockId] = (entry.connectionWeight, entry.errorMetric);
                }
            }

            if (_numericOptimizers != null)
            {
                for (int i = 0; i < Math.Min(_numericOptimizers.Count, optimizerBlocks.Count); i++)
                {
                    var blockId = optimizerBlocks[i].Id;
                    var entry = _numericOptimizers[i];
                    _optimizerMap[blockId] = (entry.connectionWeight, entry.numericOptimizer);
                }
            }
        }

        /// <summary>
        /// Executes a block-based FEM reconstruction step across the provided measurement frames,
        /// producing per-frame gradients, potentials and adjoint solutions.
        /// </summary>
        /// <param name="measurement">Measurement frames mapped to the solver ordering.</param>
        /// <returns>Collection of reconstruction frames, one entry per measurement frame.</returns>
        public List<ReconstructionFrame> Step(EITMeasurement measurement, int frameOffset = 0)
        {
            // Basic error checking
            if (measurement == null)
                throw new ArgumentNullException(nameof(measurement));
            if (_mesh == null)
                throw new InvalidOperationException("Mesh is not initialised.");
            if (_differentialEquationSolver == null)
                throw new InvalidOperationException("Differential equation solver not initialised.");
            if (_errorMetrics == null || _errorMetrics.Count == 0)
                throw new InvalidOperationException("Error metrics not configured.");

            double driveAmplitude = measurement.CurrentAmplitude.HasValue ? measurement.CurrentAmplitude.Value : 1.0;

            var electrodes = _mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var realElectrodes = electrodes.Where(e => !e.IsVirtual).ToList();
            int electrodeCount = realElectrodes.Count;
            if (electrodeCount < 2)
                throw new InvalidOperationException("At least two electrodes are required for FEM boundary conditions.");

            // Main iteration over all measurement frames
            var frames = new List<ReconstructionFrame>(measurement.Frames.Count);
            var patternDescription = measurement.PatternDescription;
            int cycleLength = patternDescription?.CycleLength ?? electrodeCount;

            for (int frameIndex = 0; frameIndex < measurement.Frames.Count; frameIndex++)
            {
                var currentFrame = measurement.Frames[frameIndex];
                int globalFrameIndex = frameOffset + frameIndex;

                // Reset electrode states
                foreach (var el in electrodes)
                {
                    el.Current = 0.0;
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.IsMeasuring = true;
                    el.Potential = 0.0;
                }

                int requestedStep = measurement.StepIndices.Count > frameIndex
                    ? measurement.StepIndices[frameIndex]
                    : globalFrameIndex;
                int normalizedStep = NormalizeStepIndex(requestedStep, Math.Max(1, cycleLength));
                var step = patternDescription?.GetStep(normalizedStep);

                // Drive electrodes are selected from the real (non-virtual) list so virtual
                // contacts can remain passive measurement completion helpers.
                var excitationPair = step?.Excitation ?? new ElectrodePair(normalizedStep, NormalizeElectrodeIndex(normalizedStep + 1, electrodeCount));
                var excitation = realElectrodes[excitationPair.First];
                excitation.IsExcitation = true;
                excitation.IsMeasuring = false;
                excitation.Current = driveAmplitude;

                var ground = realElectrodes[excitationPair.Second];
                ground.IsGround = true;
                ground.IsMeasuring = false;
                ground.Current = -driveAmplitude;

                // Create boundary condition for current frame
                var boundaryCondition = new FEMBoundaryCondition(electrodes);

                // Calculate reconstruction frame
                var reconstructionFrame = CalculateFields(boundaryCondition, currentFrame);
                
                // Adding new frame to the return list
                frames.Add(reconstructionFrame);
            }

            return frames;
        }

        private ReconstructionFrame CalculateFields(FEMBoundaryCondition boundaryCondition, double[] measurement)
        {
            // Compute the forward solution
            PotentialDistribution forwardSolution = ForwardSolve(boundaryCondition);

            // Cache the forward field gradient once per frame as it is reused in every
            // optimizer-specific gradient assembly.
            VectorField forwardGradient = FiniteElementOperators.CalculateElementWiseGradient(_mesh, forwardSolution);

            // Extract electrode potentials
            double[] electrodePotentials = _mesh.GetElectrodePotentials();

            // Clip unreasonable values
            PotentialClipper.Clip(electrodePotentials);

            List<Electrode> electrodes = _mesh.GetElectrodes().ToList();

            var projection = MeasurementProjector.Create(electrodes,
                                                         _measurementSetup,
                                                         _usePotentialDifferences,
                                                         measurement,
                                                         electrodePotentials);

            // Evaluate error metrics and adjoint solves (with caching when the same metric type is reused)
            var adjointSolutionsByBlock = EvaluateAdjointSolutions(measurement, electrodePotentials);

            // Calculate gradients of all adjoint fields just once so they can be reused across optimizers
            var adjointGradientsByBlock = CalculateAdjointGradients(adjointSolutionsByBlock);

            // Evaluate all regularizers on the current conductivity distribution once per frame
            var regularizerGradients = EvaluateRegularizers();

            // Build optimizer-specific gradients following the explicit canvas wiring
            var optimizerGradients = AssembleOptimizerGradients(forwardGradient, adjointGradientsByBlock);

            // Build optimizer-specific regularization terms with their solver link weights
            var optimizerRegularizations = AssembleOptimizerRegularizations(regularizerGradients);

            // For legacy consumers that expect a single gradient/regularization pair, blend
            // the optimizer-specific outputs using the optimizer->model weights.
            var combinedGradient = CombineOptimizerOutputs(optimizerGradients);
            var combinedRegularization = CombineOptimizerRegularizations(optimizerRegularizations);

            // Combine all components to form the reconstruction frame

            return new ReconstructionFrame(combinedGradient,
                                           forwardSolution,
                                           adjointSolutionsByBlock.Values.First(), // representative adjoint solution
                                           combinedRegularization,
                                           measurement,
                                           electrodePotentials,
                                           optimizerGradients,
                                           optimizerRegularizations);
        }

        /// <summary>
        /// Performs a simple forward solve on the given mesh using the provided boundary condition
        /// </summary>
        /// <param name="boundaryCondition">The boundary condition for the PDE.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">Throws if DE solver is not instanciated.</exception>
        private PotentialDistribution ForwardSolve(FEMBoundaryCondition boundaryCondition)
        {
            if (_differentialEquationSolver == null)
            {
                throw new InvalidOperationException("The BlockReconstructionPersistence has not been initialized.");
            }

            return _differentialEquationSolver.Solve(_mesh, boundaryCondition, null);
        }

        /// <summary>
        /// Perform adjoint solve on using the given adjoint boundary condition
        /// </summary>       
        /// <param name="adjointBoundaryCondition">The adjoint boundary condition for the PDE.</param>
        /// <param name="adjointSource">The source vector of the adjoint problem.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">If DE solver is not instanciated throws.</exception>
        private PotentialDistribution AdjointSolve(FEMBoundaryCondition adjointBoundaryCondition, double[] adjointSource)
        {
            if (_differentialEquationSolver == null)
            {
                throw new InvalidOperationException("The BlockReconstructionPersistence has not been initialized.");
            }

            // Currently convert to Complex type with zero imaginary part
            Complex[] tmp = new Complex[adjointSource.Length];
            for (int i = 0; i < adjointSource.Length; i++)
                tmp[i] = new Complex(adjointSource[i], 0);


            return _differentialEquationSolver.Solve(_mesh, adjointBoundaryCondition, tmp);
        }

        /// <summary>
        /// Evaluates the adjoint source for each error metric and constructs the corresponding boundary conditions.
        /// </summary>
        /// <param name="measurement">The corresponding measurement to the problem.</param>
        /// <param name="simulatedMeasurement">The simulated electrode potentials. Must align with the measurements excitation!</param>
        /// <returns></returns>
        private Dictionary<string, PotentialDistribution> EvaluateAdjointSolutions(double[] measurement, double[] simulatedMeasurement)
        {
            var electrodes = _mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            // Reset electrode states for adjoint solves
            foreach (var electrode in electrodes)
            {
                electrode.IsExcitation = false;
                electrode.IsGround = false;
                electrode.IsMeasuring = true;
            }

            // Cache adjoint solutions by error metric type to avoid duplicate evaluation
            var adjointCache = new Dictionary<Type, PotentialDistribution>();
            var solutions = new Dictionary<string, PotentialDistribution>();

            Parallel.ForEach(_errorMetricMap, kvp =>
            {
                var errorMetricType = kvp.Value.errorMetric.GetType();

                // Ensure only one evaluation per metric type
                PotentialDistribution adjointSolution;
                lock (adjointCache)
                {
                    if (adjointCache.TryGetValue(errorMetricType, out var cached))
                    {
                        lock (solutions)
                        {
                            solutions[kvp.Key] = cached;
                        }
                        return;
                    }
                }

                var adjointSource = kvp.Value.errorMetric.EvaluateAdjointSource(_mesh, measurement, simulatedMeasurement);
                var adjointBoundaryCondition = new FEMBoundaryCondition(electrodes);
                adjointBoundaryCondition.SetElectrodePotentials(adjointSource);
                adjointSolution = AdjointSolve(adjointBoundaryCondition, adjointBoundaryCondition.GetElectrodePotentials());

                lock (adjointCache)
                {
                    adjointCache[errorMetricType] = adjointSolution;
                }

                lock (solutions)
                {
                    solutions[kvp.Key] = adjointSolution;
                }
            });

            return solutions;
        }

        private Dictionary<string, VectorField> CalculateAdjointGradients(Dictionary<string, PotentialDistribution> adjointSolutionsByBlock)
        {
            var adjointGradients = new Dictionary<string, VectorField>();

            Parallel.ForEach(adjointSolutionsByBlock, kvp =>
            {
                var gradient = FiniteElementOperators.CalculateElementWiseGradient(_mesh, kvp.Value);
                lock (adjointGradients)
                {
                    adjointGradients[kvp.Key] = gradient;
                }
            });

            return adjointGradients;
        }

        private Dictionary<string, ConductivityDistribution> AssembleOptimizerGradients(VectorField forwardGradient,
                                                                                        Dictionary<string, VectorField> adjointGradientsByBlock)
        {
            var optimizerGradients = new Dictionary<string, ConductivityDistribution>();
            var elements = _mesh.GetElements().Cast<FEMElement>().ToList();

            foreach (var optimizer in _optimizerMap)
            {
                var optimizerId = optimizer.Key;
                var connectedErrorMetrics = _connections?
                    .Where(c => c.TargetId == optimizerId && c.SourceType == BlockType.ErrorMetric)
                    .Select(c => c.SourceId)
                    .ToList() ?? new List<string>();

                var gradientAccumulator = new Dictionary<int, double>();

                foreach (var errorMetricId in connectedErrorMetrics)
                {
                    if (!adjointGradientsByBlock.TryGetValue(errorMetricId, out var adjointGradient))
                        continue;

                    if (!_errorMetricMap.TryGetValue(errorMetricId, out var descriptor))
                        continue;

                    double weight = descriptor.weight;

                    Parallel.ForEach(elements, element =>
                    {
                        var gradPhi = forwardGradient.GetVector(element.Id);
                        var gradMu = adjointGradient.GetVector(element.Id);
                        double dotProduct = -(gradPhi.X * gradMu.X + gradPhi.Y * gradMu.Y) * element.Area;
                        double weighted = weight * dotProduct;

                        lock (gradientAccumulator)
                        {
                            if (gradientAccumulator.ContainsKey(element.Id))
                                gradientAccumulator[element.Id] += weighted;
                            else
                                gradientAccumulator[element.Id] = weighted;
                        }
                    });
                }

                optimizerGradients[optimizerId] = new ConductivityDistribution(gradientAccumulator);
            }

            return optimizerGradients;
        }

        private Dictionary<string, ConductivityDistribution> EvaluateRegularizers()
        {
            var currentDistribution = _mesh?.GetConductivityDistribution() ?? _initialDistribution
                                       ?? throw new InvalidOperationException("No conductivity distribution available for regularization evaluation.");

            var regularizations = new Dictionary<string, ConductivityDistribution>();

            Parallel.ForEach(_regularizerMap, kvp =>
            {
                var weighted = kvp.Value.regulizer.EvaluateGradient(_mesh, currentDistribution);

                // Apply solver->regularizer connection weight here
                foreach (var elementId in weighted.IdValuePairs.Keys.ToList())
                {
                    double value = weighted.GetValue(elementId);
                    weighted.SetValue(elementId, kvp.Value.weight * value);
                }

                lock (regularizations)
                {
                    regularizations[kvp.Key] = weighted;
                }
            });

            return regularizations;
        }

        private Dictionary<string, ConductivityDistribution> AssembleOptimizerRegularizations(Dictionary<string, ConductivityDistribution> regularizerGradients)
        {
            var optimizerRegularizations = new Dictionary<string, ConductivityDistribution>();

            foreach (var optimizer in _optimizerMap)
            {
                var optimizerId = optimizer.Key;
                var connectedRegularizers = _connections?
                    .Where(c => c.TargetId == optimizerId && c.SourceType == BlockType.Regularizer)
                    .Select(c => c.SourceId)
                    .ToList() ?? new List<string>();

                var accumulator = new Dictionary<int, double>();

                foreach (var regId in connectedRegularizers)
                {
                    if (!regularizerGradients.TryGetValue(regId, out var reg))
                        continue;

                    foreach (var kvp in reg.IdValuePairs)
                    {
                        if (accumulator.ContainsKey(kvp.Key))
                            accumulator[kvp.Key] += kvp.Value;
                        else
                            accumulator[kvp.Key] = kvp.Value;
                    }
                }

                optimizerRegularizations[optimizerId] = new ConductivityDistribution(accumulator);
            }

            return optimizerRegularizations;
        }

        private ConductivityDistribution CombineOptimizerOutputs(IReadOnlyDictionary<string, ConductivityDistribution> optimizerGradients)
        {
            var combined = new Dictionary<int, double>();
            double totalWeight = 0.0;

            foreach (var kvp in optimizerGradients)
            {
                if (!_optimizerMap.TryGetValue(kvp.Key, out var optimizerDescriptor))
                    continue;

                totalWeight += optimizerDescriptor.weight;

                foreach (var value in kvp.Value.IdValuePairs)
                {
                    if (combined.ContainsKey(value.Key))
                        combined[value.Key] += optimizerDescriptor.weight * value.Value;
                    else
                        combined[value.Key] = optimizerDescriptor.weight * value.Value;
                }
            }

            if (totalWeight > 0)
            {
                foreach (var id in combined.Keys.ToList())
                    combined[id] /= totalWeight;
            }

            return new ConductivityDistribution(combined);
        }

        private ConductivityDistribution CombineOptimizerRegularizations(IReadOnlyDictionary<string, ConductivityDistribution> optimizerRegularizations)
        {
            var combined = new Dictionary<int, double>();
            double totalWeight = 0.0;

            foreach (var kvp in optimizerRegularizations)
            {
                if (!_optimizerMap.TryGetValue(kvp.Key, out var optimizerDescriptor))
                    continue;

                totalWeight += optimizerDescriptor.weight;

                foreach (var value in kvp.Value.IdValuePairs)
                {
                    if (combined.ContainsKey(value.Key))
                        combined[value.Key] += optimizerDescriptor.weight * value.Value;
                    else
                        combined[value.Key] = optimizerDescriptor.weight * value.Value;
                }
            }

            if (totalWeight > 0)
            {
                foreach (var id in combined.Keys.ToList())
                    combined[id] /= totalWeight;
            }

            return new ConductivityDistribution(combined);
        }

        private static int NormalizeStepIndex(int stepIndex, int cycleLength)
        {
            int normalized = stepIndex % cycleLength;
            return normalized < 0 ? normalized + cycleLength : normalized;
        }

        private static int NormalizeElectrodeIndex(int index, int electrodeCount)
        {
            int normalized = index % electrodeCount;
            return normalized < 0 ? normalized + electrodeCount : normalized;
        }
    }
}
