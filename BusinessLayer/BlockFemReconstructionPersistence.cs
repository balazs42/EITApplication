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
        private List<(double connectionWeight, IRegularizer regulizer)>? _regularizers = null;
        private List<(double connectionWeight, IErrorMetric errorMetric)>? _errorMetrics = null;
        private List<(double connectionWeight, INumericOptimizer numericOptimizer)>? _numericOptimizers = null;

        private CompleteReconstructionConfiguration? _completeReconstructionConfiguration = null;

        private InitialDistributionTypes _initialDistributionType = InitialDistributionTypes.Homogeneous;
        private ConductivityDistribution _originalDistribution;
        private ConductivityDistribution _initialDistribution;

        private ElectrodeMeasurementSetup _measurementSetup = ElectrodeMeasurementSetup.Active;
        private bool _usePotentialDifferences = false;

        public void Initialize(CompleteReconstructionConfiguration configuration)
        {
            // TODO: properly extract and initialize all components from the configuration
            _completeReconstructionConfiguration = configuration;
        }

        public List<ReconstructionFrame> Step(EITMeasurement measurement)
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
            int electrodeCount = electrodes.Count;
            if (electrodeCount < 2)
                throw new InvalidOperationException("At least two electrodes are required for FEM boundary conditions.");

            // Main iteration over all measurement frames
            var frames = new List<ReconstructionFrame>(measurement.Frames.Count);

            for (int frameIndex = 0; frameIndex < measurement.Frames.Count; frameIndex++)
            {
                var currentFrame = measurement.Frames[frameIndex];

                // Reset electrode states
                foreach (var el in electrodes)
                {
                    el.Current = 0.0;
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.IsMeasuring = true;
                    el.Potential = 0.0;
                }

                // Set excitation and ground electrodes for current frame
                // TODO: generic adaptation for different patterns
                int excitationIndex = frameIndex % electrodeCount;
                int groundIndex = (frameIndex + 1) % electrodeCount;

                var excitation = electrodes[excitationIndex];
                excitation.IsExcitation = true;
                excitation.IsMeasuring = false;
                excitation.Current = driveAmplitude;

                var ground = electrodes[groundIndex];
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

            // Evaluate error metrics
            List<FEMBoundaryCondition> adjointBoundaryConditions = EvaluateAdjointSources(measurement, electrodePotentials);

            List<PotentialDistribution> adjointSolutions = new List<PotentialDistribution>();

            // Perfrom adjoint solves over all adjoint sources
            Parallel.ForEach(adjointBoundaryConditions, adjointBoundaryCondition =>
            {
                var adjointSolution = AdjointSolve(adjointBoundaryCondition, adjointBoundaryCondition.GetElectrodePotentials());
                lock (adjointSolutions)
                {
                    adjointSolutions.Add(adjointSolution);
                }
            });

            List<double> connectionWeights = GetGradientWeights();
            ConductivityDistribution conductivityGradient = CalculateCombinedGradient(forwardSolution, adjointSolutions, connectionWeights);

            ConductivityDistribution regularizedDistribution = CalculateCombinedRegularization();

            // Combine all components to form the reconstruction frame

            return new ReconstructionFrame(conductivityGradient,
                                           forwardSolution,
                                           adjointSolutions.First(), // For now only return the first adjoint solution
                                           regularizedDistribution,
                                           measurement,
                                           electrodePotentials);
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
        private List<FEMBoundaryCondition> EvaluateAdjointSources(double[] measurement, double[] simulatedMeasurement)
        {
            var electrodes = _mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            // Set electrode states for adjoint solve
            foreach (var electrode in electrodes)
            {
                electrode.IsExcitation = false;
                electrode.IsGround = false;
                electrode.IsMeasuring = true;
            }

            List<double[]> adjointSources = new List<double[]>();

            Parallel.ForEach(_errorMetrics, (weightErrorMetric) =>
            {
                var (weight, errorMetric) = weightErrorMetric;
                var adjointSource = errorMetric.EvaluateAdjointSource(_mesh, measurement, simulatedMeasurement);
                lock (adjointSources)
                {
                    adjointSources.Add(adjointSource);
                }
            });

            List<FEMBoundaryCondition> adjointBoundaryConditions = new List<FEMBoundaryCondition>();

            foreach (var adjointSource in adjointSources)
            {
                var adjointBoundaryCondition = new FEMBoundaryCondition(electrodes);
                adjointBoundaryCondition.SetElectrodePotentials(adjointSource);
                adjointBoundaryConditions.Add(adjointBoundaryCondition);
            }

            return adjointBoundaryConditions;
        }

        /// <summary>
        /// Returns the weights for each error metric to be used in the gradient calculation.
        /// They should be in the same order as the error metrics in the configuration.
        /// </summary>
        /// <returns></returns>
        private List<double> GetGradientWeights()
        {
            List<double> weights = new List<double>();
            foreach (var (weight, errorMetric) in _errorMetrics)
                weights.Add(weight);
            return weights;
        }

        /// <summary>
        /// Calculates the gradients of the forward and adjoint solutions and combines them into a single conductivity gradient.
        /// Uses the configuration defined weights for the gradients.
        /// </summary>
        /// <param name="forwardGradient">The forward projection potential map.</param>
        /// <param name="adjointGradients">The adjoint solve's potential maps.</param>
        /// <param name="connectionWeights">The connection weights for each adjoint gradient.</param>
        /// <returns>The combination of the gradients in a conductivitiy distribution object</returns>
        private ConductivityDistribution CalculateCombinedGradient(PotentialDistribution forwardSolution, List<PotentialDistribution> adjointSolutions, List<double> connectionWeights)
        {
            if (adjointSolutions.Count != connectionWeights.Count)
            {
                throw new ArgumentException("The number of adjoint solutions must match the number of connection weights.");
            }

            // Calculate field gradients: ∇φ and ∇μ on elements
            VectorField forwardGradient = FiniteElementOperators.CalculateElementWiseGradient(_mesh, forwardSolution);
            List<VectorField> adjointGradients = new List<VectorField>();

            Parallel.ForEach(adjointSolutions, mu =>
            {
                var adjointGradient = FiniteElementOperators.CalculateElementWiseGradient(_mesh, mu);
                lock (adjointGradients)
                {
                    adjointGradients.Add(adjointGradient);
                }
            });

            List<Dictionary<int, double>> gradientDotProducts = new List<Dictionary<int, double>>();

            var elements = _mesh.GetElements().Cast<FEMElement>().ToList();

            for (int i = 0; i < adjointGradients.Count(); i++)
            {
                // Get current adjoint gradient field to dot product with forward gradient
                VectorField adjointGradient = adjointGradients[i];

                // Get the current weigth for the gradient
                double currentWeight = connectionWeights[i];

                Dictionary<int, double> gradientValues = new Dictionary<int, double>();

                // Compute dot product on each element:  −(∇μ·∇φ)·Area per element
                Parallel.ForEach(elements, element =>
                {
                    var gradPhi = forwardGradient.GetVector(element.Id);
                    var gradMu = adjointGradient.GetVector(element.Id);
                    // −(∇μ·∇φ)·Area
                    double dotProduct = -(gradPhi.X * gradMu.X + gradPhi.Y * gradMu.Y) * element.Area;

                    // Scale with w_i: w_i * −(∇μ·∇φ)·Area
                    gradientValues[element.Id] = currentWeight * dotProduct;
                });

                gradientDotProducts.Add(gradientValues);
            }

            // Combined gradient container
            Dictionary<int, double> combinedGradientValues = new Dictionary<int, double>(gradientDotProducts.First().Count());

            foreach (var gradientDotProduct in gradientDotProducts)
            {
                foreach (var kvp in gradientDotProduct)
                {
                    if (combinedGradientValues.ContainsKey(kvp.Key))
                        combinedGradientValues[kvp.Key] += kvp.Value;
                    else
                        combinedGradientValues[kvp.Key] = kvp.Value;
                }
            }

            return new ConductivityDistribution(combinedGradientValues);
        }

        /// <summary>
        /// Calculate the combined regularization from all regularizers defined in the configuration.
        /// Weigths each regularization term accordingly, weigths should be ordered as the regularizers.
        /// </summary>
        /// <returns></returns>
        private ConductivityDistribution CalculateCombinedRegularization()
        {
            List<ConductivityDistribution> regularizations = new List<ConductivityDistribution>();

            Parallel.ForEach(_regularizers, regulizerEntry =>
            {
                var (weight, regulizer) = regulizerEntry;
                var regularization = regulizer.EvaluateGradient(_mesh, _initialDistribution);

                // Scale regularization with its weight
                foreach (var elementId in regularization.IdValuePairs.Keys.ToList())
                {
                    double value = regularization.GetValue(elementId);
                    regularization.SetValue(elementId, weight * value);
                }
                lock (regularizations)
                {
                    regularizations.Add(regularization);
                }
            });

            // Combined regularization container
            Dictionary<int, double> combinedRegularizationValues = new Dictionary<int, double>();

            Parallel.ForEach(regularizations, regularization =>
            {
                foreach (var kvp in regularization.IdValuePairs)
                {
                    lock (combinedRegularizationValues)
                    {
                        if (combinedRegularizationValues.ContainsKey(kvp.Key))
                            combinedRegularizationValues[kvp.Key] += kvp.Value;
                        else
                            combinedRegularizationValues[kvp.Key] = kvp.Value;
                    }
                }
            });

            return new ConductivityDistribution(combinedRegularizationValues);
        }
    }
}