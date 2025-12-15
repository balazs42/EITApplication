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
    /// <summary>
    /// Core persistence layer for block-based Finite Element Method (FEM) reconstruction in Electrical Impedance Tomography (EIT).
    /// 
    /// This class orchestrates the complete mathematical pipeline for EIT image reconstruction:
    /// 1. Manages the FEM mesh and solver instances
    /// 2. Coordinates forward problem solving (computing potentials from known conductivity)
    /// 3. Executes adjoint method for gradient computation (sensitivity analysis)
    /// 4. Assembles gradients following the block-based canvas wiring architecture
    /// 5. Applies regularization to stabilize the inverse problem
    /// 
    /// The "block-based" architecture allows users to visually configure reconstruction pipelines
    /// by connecting blocks (error metrics, regularizers, optimizers) with weighted connections.
    /// This class materializes that visual configuration into executable reconstruction logic.
    /// </summary>
    public class BlockFemReconstructionPersistence
    {
        // ==================== CORE FEM COMPONENTS ====================
        
        /// <summary>
        /// The finite element mesh discretizing the 2D domain into triangular elements.
        /// Contains nodes, elements, electrodes, and the current conductivity distribution.
        /// </summary>
        private FEMMesh? _mesh = null;
        
        /// <summary>
        /// Solver for the forward problem: given conductivity σ and boundary conditions,
        /// computes the potential distribution φ satisfying ∇·(σ∇φ) = 0.
        /// Also used for adjoint problem solving with modified boundary conditions.
        /// </summary>
        private IDifferentialEquationSolver? _differentialEquationSolver = null;
        
        /// <summary>
        /// Numeric solver for linear systems (e.g., LU decomposition, iterative methods).
        /// Used internally by the differential equation solver.
        /// </summary>
        private INumericSolver? _numericSolver = null;
        
        // ==================== RECONSTRUCTION COMPONENTS ====================
        
        /// <summary>
        /// List of regularizers with their block IDs and connection weights from the solver block.
        /// Regularizers penalize unrealistic conductivity distributions (e.g., roughness, sparsity).
        /// Each entry: (blockId, solver→regularizer weight, regularizer instance)
        /// </summary>
        private List<(string id, double connectionWeight, IRegularizer regulizer)>? _regularizers = null;
        
        /// <summary>
        /// List of error metrics with their block IDs and connection weights from the solver block.
        /// Error metrics measure data misfit (difference between measured and simulated electrode readings).
        /// Each entry: (blockId, solver→errorMetric weight, errorMetric instance)
        /// Common examples: L2 norm, relative error, weighted least squares
        /// </summary>
        private List<(string id, double connectionWeight, IErrorMetric errorMetric)>? _errorMetrics = null;
        
        /// <summary>
        /// List of numeric optimizers with their block IDs and connection weights to the model block.
        /// Optimizers update the conductivity estimate based on gradients (e.g., gradient descent, Adam, L-BFGS).
        /// Each entry: (blockId, optimizer→model weight, optimizer instance)
        /// </summary>
        private List<(string id, double connectionWeight, INumericOptimizer numericOptimizer)>? _numericOptimizers = null;

        /// <summary>
        /// The complete configuration loaded from the visual block canvas.
        /// Contains all block definitions, their parameters, and inter-block connections.
        /// </summary>
        private CompleteReconstructionConfiguration? _completeReconstructionConfiguration = null;

        /// <summary>
        /// Materialized runtime context derived from the block configuration. Contains
        /// the mesh, solver instances and weighted reconstruction components.
        /// Exposed publicly so services can access mesh, solvers, and distributions.
        /// </summary>
        public ReconstructionRuntimeContext? RuntimeContext { get; private set; }

        // ==================== CONDUCTIVITY DISTRIBUTIONS ====================
        
        /// <summary>
        /// Type of initial guess for conductivity (Homogeneous, Random, FromFile, etc.).
        /// Determines how the optimization is seeded before the first iteration.
        /// </summary>
        private InitialDistributionTypes _initialDistributionType = InitialDistributionTypes.Homogeneous;
        
        /// <summary>
        /// Ground truth conductivity distribution (if available, e.g., from simulation).
        /// Used for validation and computing reconstruction errors, not for actual reconstruction.
        /// </summary>
        private ConductivityDistribution _originalDistribution;
        
        /// <summary>
        /// Current conductivity estimate. Updated after each optimization step.
        /// The reconstruction algorithm iteratively refines this to match measured data.
        /// </summary>
        private ConductivityDistribution _initialDistribution;

        // ==================== MEASUREMENT CONFIGURATION ====================
        
        /// <summary>
        /// Measurement setup: which electrodes are used for voltage measurement.
        /// - Active: measure only on active (non-driving) electrodes
        /// - Adjacent: measure on electrodes adjacent to drive pair
        /// - All: measure on all electrodes
        /// </summary>
        private ElectrodeMeasurementSetup _measurementSetup = ElectrodeMeasurementSetup.Active;
        
        /// <summary>
        /// If true, use potential differences (e.g., V_i - V_ref) instead of absolute potentials.
        /// This improves numerical stability and removes the arbitrary reference potential.
        /// </summary>
        private bool _usePotentialDifferences = false;

        // ==================== BLOCK WIRING ARCHITECTURE ====================
        
        /// <summary>
        /// All weighted connections between blocks in the visual canvas.
        /// Each connection has: sourceId, targetId, sourceType, targetType, weight.
        /// These connections define the data flow for gradient assembly:
        /// ErrorMetric → Optimizer (data misfit gradient contribution)
        /// Regularizer → Optimizer (regularization gradient contribution)
        /// Optimizer → Model (final update to conductivity)
        /// </summary>
        private IReadOnlyList<WeightedConnectionSnapshot>? _connections;
        
        /// <summary>
        /// Fast lookup: blockId → (weight from solver, regularizer instance)
        /// Allows efficient gradient assembly by following explicit canvas wiring.
        /// </summary>
        private Dictionary<string, (double weight, IRegularizer regulizer)> _regularizerMap = new();
        
        /// <summary>
        /// Fast lookup: blockId → (weight from solver, errorMetric instance)
        /// Enables quick access to error metrics when computing adjoint sources.
        /// </summary>
        private Dictionary<string, (double weight, IErrorMetric errorMetric)> _errorMetricMap = new();
        
        /// <summary>
        /// Fast lookup: blockId → (weight to model, optimizer instance)
        /// Used to route gradients to the correct optimizer and blend multiple optimizer outputs.
        /// </summary>
        private Dictionary<string, (double weight, INumericOptimizer optimizer)> _optimizerMap = new();

        /// <summary>
        /// Flag to prevent redundant initialization. Once true, Initialize() becomes a no-op.
        /// </summary>
        private bool _initialized = false; 

        /// <summary>
        /// Exposes the active differential equation solver so services can share it with
        /// measurement preparation pipelines (e.g., simulating reference measurements).
        /// </summary>
        public IDifferentialEquationSolver? DifferentialEquationSolver => _differentialEquationSolver;

        /// <summary>
        /// Initializes the reconstruction persistence from a complete block configuration.
        /// 
        /// This method:
        /// 1. Materializes the visual block canvas into runtime objects (mesh, solvers, optimizers)
        /// 2. Extracts conductivity distributions (original for validation, initial for starting guess)
        /// 3. Builds lookup dictionaries mapping block IDs to instances for fast gradient assembly
        /// 4. Validates that all required components are present
        /// 
        /// Must be called before any reconstruction steps can execute.
        /// </summary>
        /// <param name="configuration">Complete block-based configuration from the canvas</param>
        /// <exception cref="ArgumentNullException">If configuration is null</exception>
        /// <exception cref="InvalidOperationException">If materialization fails or required components are missing</exception>
        public void Initialize(CompleteReconstructionConfiguration configuration)
        {
            // Guard: prevent re-initialization which could corrupt state
            if(_initialized)
                return;

            _completeReconstructionConfiguration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            // Materialize converts the configuration DTO into actual runtime instances:
            // - Creates FEM mesh from geometry and discretization parameters
            // - Instantiates solver, error metrics, regularizers, optimizers based on block types
            // - Applies configured weights and parameters to each component
            RuntimeContext = ReconstructionConfigurationMaterializer.Materialize(configuration);

            // Extract core components from the materialized context
            _mesh = RuntimeContext.RuntimeMesh;
            _differentialEquationSolver = RuntimeContext.RuntimeDifferentialEquationSolver;
            _numericSolver = RuntimeContext.RuntimeNumericSolver;
            _regularizers = RuntimeContext.Regularizers;
            _errorMetrics = RuntimeContext.ErrorMetrics;
            _numericOptimizers = RuntimeContext.NumericOptimizers;
            _initialDistributionType = RuntimeContext.InitialDistributionType;
            
            // These distributions must exist - throw if missing to fail fast
            _originalDistribution = RuntimeContext.OriginalDistribution ?? throw new InvalidOperationException("Original distribution missing from runtime context.");
            _initialDistribution = RuntimeContext.InitialDistribution ?? throw new InvalidOperationException("Initial distribution missing from runtime context.");
            
            _measurementSetup = RuntimeContext.MeasurementSetup;
            _usePotentialDifferences = RuntimeContext.UsePotentialDifferences;
            _connections = RuntimeContext.AllConnections;

            // Build fast lookup dictionaries for gradient assembly
            // This maps block IDs to their runtime instances so we can follow canvas connections efficiently
            BuildLookupMaps();

            _initialized = true;
        }

        /// <summary>
        /// Updates the internally tracked conductivity distribution to keep regularization
        /// and gradient calculations aligned with the latest optimization step.
        /// 
        /// This synchronization is critical because:
        /// - Regularizers evaluate gradients with respect to the current estimate
        /// - The mesh needs the current σ for forward/adjoint solves
        /// - Next iteration's calculations depend on the updated conductivity
        /// </summary>
        /// <param name="updated">Most recent conductivity estimate from optimization step</param>
        /// <exception cref="ArgumentNullException">If updated is null</exception>
        public void UpdateCurrentDistribution(ConductivityDistribution updated)
        {
            _initialDistribution = updated ?? throw new ArgumentNullException(nameof(updated));
            _mesh?.SetConductivityDistribution(updated);
        }

        /// <summary>
        /// Prepares fast lookup dictionaries from block ids to their runtime instances
        /// so that later gradient assembly can follow the explicit canvas wiring.
        /// 
        /// The visual canvas allows arbitrary connections between blocks. To implement this efficiently:
        /// 1. We extract blocks by type (Regularizer, ErrorMetric, Optimizer)
        /// 2. Match them positionally with the materialized runtime instances
        /// 3. Build dictionaries: blockId → (connection weight, instance)
        /// 
        /// This enables O(1) lookup during gradient assembly when following connection paths.
        /// </summary>
        private void BuildLookupMaps()
        {
            // Initialize empty dictionaries
            _regularizerMap = new Dictionary<string, (double weight, IRegularizer regulizer)>();
            _errorMetricMap = new Dictionary<string, (double weight, IErrorMetric errorMetric)>();
            _optimizerMap = new Dictionary<string, (double weight, INumericOptimizer optimizer)>();

            if (_completeReconstructionConfiguration == null)
                return;

            // Extract blocks by type from the configuration
            // The canvas stores blocks in a flat list; we filter by type
            var regularizerBlocks = _completeReconstructionConfiguration.Blocks.Where(b => b.Type == BlockType.Regularizer).ToList();
            var errorBlocks = _completeReconstructionConfiguration.Blocks.Where(b => b.Type == BlockType.ErrorMetric).ToList();
            var optimizerBlocks = _completeReconstructionConfiguration.Blocks.Where(b => b.Type == BlockType.Optimizer).ToList();

            // Match regularizer blocks to materialized instances
            // We assume positional correspondence (index i in blocks matches index i in materialized list)
            if (_regularizers != null)
            {
                for (int i = 0; i < Math.Min(_regularizers.Count, regularizerBlocks.Count); i++)
                {
                    var blockId = regularizerBlocks[i].Id;
                    var entry = _regularizers[i];
                    // Store the connection weight from solver→regularizer and the instance
                    _regularizerMap[blockId] = (entry.connectionWeight, entry.regulizer);
                }
            }

            // Match error metric blocks to materialized instances
            if (_errorMetrics != null)
            {
                for (int i = 0; i < Math.Min(_errorMetrics.Count, errorBlocks.Count); i++)
                {
                    var blockId = errorBlocks[i].Id;
                    var entry = _errorMetrics[i];
                    // Store the connection weight from solver→errorMetric and the instance
                    _errorMetricMap[blockId] = (entry.connectionWeight, entry.errorMetric);
                }
            }

            // Match optimizer blocks to materialized instances
            if (_numericOptimizers != null)
            {
                for (int i = 0; i < Math.Min(_numericOptimizers.Count, optimizerBlocks.Count); i++)
                {
                    var blockId = optimizerBlocks[i].Id;
                    var entry = _numericOptimizers[i];
                    // Store the connection weight from optimizer→model and the instance
                    _optimizerMap[blockId] = (entry.connectionWeight, entry.numericOptimizer);
                }
            }
        }

        /// <summary>
        /// Executes a block-based FEM reconstruction step across the provided measurement frames,
        /// producing per-frame gradients, potentials and adjoint solutions.
        /// 
        /// HIGH-LEVEL ALGORITHM:
        /// For each measurement frame (one drive pattern step):
        ///   1. Configure electrode boundary conditions (drive currents, ground)
        ///   2. Solve forward problem: ∇·(σ∇φ) = 0 with current BCs → get φ (potential distribution)
        ///   3. Extract simulated electrode potentials and compare with measured data
        ///   4. For each error metric:
        ///      a. Compute adjoint source (sensitivity of error to electrode potentials)
        ///      b. Solve adjoint problem: ∇·(σ∇μ) = adjoint_source → get μ (adjoint field)
        ///   5. Compute gradients ∂J/∂σ = -∇φ · ∇μ for each element (sensitivity of error to conductivity)
        ///   6. Evaluate regularization gradients ∂R/∂σ
        ///   7. Assemble optimizer-specific gradients following canvas wiring
        ///   8. Package results into ReconstructionFrame for service layer
        /// 
        /// The adjoint method is key to computational efficiency:
        /// - Direct sensitivity computation would require O(N_params × N_measurements) forward solves
        /// - Adjoint method needs only O(N_measurements) forward + adjoint solve pairs
        /// - For EIT with thousands of elements, this is orders of magnitude faster
        /// </summary>
        /// <param name="measurement">Measurement frames with current injection pattern and electrode readings</param>
        /// <param name="frameOffset">Global frame index offset for pattern step calculation</param>
        /// <returns>Collection of reconstruction frames, one entry per measurement frame</returns>
        /// <exception cref="ArgumentNullException">If measurement is null</exception>
        /// <exception cref="InvalidOperationException">If mesh, solver, or error metrics not initialized</exception>
        public List<ReconstructionFrame> Step(EITMeasurement measurement, int frameOffset = 0)
        {
            // Validate inputs and state
            if (measurement == null)
                throw new ArgumentNullException(nameof(measurement));
            if (_mesh == null)
                throw new InvalidOperationException("Mesh is not initialised.");
            if (_differentialEquationSolver == null)
                throw new InvalidOperationException("Differential equation solver not initialised.");
            if (_errorMetrics == null || _errorMetrics.Count == 0)
                throw new InvalidOperationException("Error metrics not configured.");

            // Extract drive current amplitude (typically 1 mA for EIT)
            double driveAmplitude = measurement.CurrentAmplitude.HasValue ? measurement.CurrentAmplitude.Value : 1.0;

            // Get electrodes from mesh
            // Virtual electrodes may exist for numerical stability (reference potentials)
            // Real electrodes are the physical contacts on the boundary
            var electrodes = _mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var realElectrodes = electrodes.Where(e => !e.IsVirtual).ToList();
            int electrodeCount = realElectrodes.Count;
            if (electrodeCount < 2)
                throw new InvalidOperationException("At least two electrodes are required for FEM boundary conditions.");

            // Prepare frame collection (one frame per measurement in the input)
            var frames = new List<ReconstructionFrame>(measurement.Frames.Count);
            var patternDescription = measurement.PatternDescription;
            int cycleLength = patternDescription?.CycleLength ?? electrodeCount;

            // Main iteration over all measurement frames
            // In EIT, a typical cycle has N frames for N electrodes (adjacent drive pattern)
            for (int frameIndex = 0; frameIndex < measurement.Frames.Count; frameIndex++)
            {
                var currentFrame = measurement.Frames[frameIndex];
                int globalFrameIndex = frameOffset + frameIndex;

                // Reset electrode states to default before configuring this frame
                // This ensures clean state for each boundary condition setup
                foreach (var el in electrodes)
                {
                    el.Current = 0.0;           // No current flow by default
                    el.IsExcitation = false;    // Not a current source
                    el.IsGround = false;         // Not grounded
                    el.IsMeasuring = true;       // Measure voltage by default
                    el.Potential = 0.0;          // Reset potential
                }

                // Determine which drive pattern step this frame corresponds to
                // Step index defines which electrode pair receives current injection
                int requestedStep = measurement.StepIndices.Count > frameIndex
                    ? measurement.StepIndices[frameIndex]
                    : globalFrameIndex;
                int normalizedStep = NormalizeStepIndex(requestedStep, Math.Max(1, cycleLength));
                var step = patternDescription?.GetStep(normalizedStep);

                // Configure excitation (drive) electrodes for this step
                // Drive electrodes are selected from the real (non-virtual) list so virtual
                // contacts can remain passive measurement completion helpers.
                // Typical EIT pattern: inject +I on electrode i, -I on electrode i+1 (adjacent pattern)
                var excitationPair = step?.Excitation ?? new ElectrodePair(normalizedStep, NormalizeElectrodeIndex(normalizedStep + 1, electrodeCount));
                
                // Positive current source electrode
                var excitation = realElectrodes[excitationPair.First];
                excitation.IsExcitation = true;
                excitation.IsMeasuring = false;  // Don't measure voltage on drive electrode
                excitation.Current = driveAmplitude;

                // Negative current sink (ground) electrode
                var ground = realElectrodes[excitationPair.Second];
                ground.IsGround = true;
                ground.IsMeasuring = false;      // Don't measure voltage on ground electrode
                ground.Current = -driveAmplitude;

                // Create boundary condition object encapsulating electrode states
                // This will be used by the FEM solver to set up the linear system
                var boundaryCondition = new FEMBoundaryCondition(electrodes);

                // Calculate reconstruction frame for this measurement
                // This is where the core FEM forward/adjoint computation happens
                var reconstructionFrame = CalculateFields(boundaryCondition, currentFrame);
                
                // Add frame to return collection
                frames.Add(reconstructionFrame);
            }

            return frames;
        }

        /// <summary>
        /// Performs the core field calculations for one measurement frame.
        /// 
        /// MATHEMATICAL OVERVIEW:
        /// 1. Forward solve: Given current conductivity σ and boundary conditions (current injection),
        ///    solve the elliptic PDE: ∇·(σ∇φ) = 0 to get potential distribution φ
        /// 
        /// 2. Measurement projection: Compare simulated electrode potentials φ_sim with measured φ_meas
        ///    Apply appropriate projection (active electrodes only, potential differences, etc.)
        /// 
        /// 3. Adjoint solve: For each error metric E(φ_sim, φ_meas):
        ///    - Compute adjoint source b = ∂E/∂φ (sensitivity of error to potentials)
        ///    - Solve adjoint PDE: ∇·(σ∇μ) = b to get adjoint field μ
        ///   4. Compute gradients ∂J/∂σ = -∇φ · ∇μ for each element (sensitivity of error to conductivity)
        ///   5. Evaluate regularization gradients ∂R/∂σ
        ///   6. Assemble optimizer-specific gradients following canvas wiring
        ///   7. Package results into ReconstructionFrame for service layer
        /// 
        /// The adjoint method is key to computational efficiency:
        /// - Direct sensitivity computation would require O(N_params × N_measurements) forward solves
        /// - Adjoint method needs only O(N_measurements) forward + adjoint solve pairs
        /// - For EIT with thousands of elements, this is orders of magnitude faster
        /// </summary>
        /// <param name="boundaryCondition">Current injection pattern (which electrodes drive/measure)</param>
        /// <param name="measurement">Measured electrode potentials from hardware or simulation</param>
        /// <returns>ReconstructionFrame containing gradients, fields, and intermediate results</returns>
        private ReconstructionFrame CalculateFields(FEMBoundaryCondition boundaryCondition, double[] measurement)
        {
            // ========== STEP 1: FORWARD SOLVE ==========
            // Solve ∇·(σ∇φ) = 0 with current boundary conditions
            // This gives us the potential distribution throughout the domain
            PotentialDistribution forwardSolution = ForwardSolve(boundaryCondition);

            // ========== STEP 2: COMPUTE FORWARD GRADIENT ==========
            // Calculate ∇φ at each element center (needed for adjoint gradient computation)
            // Cache this once per frame since it's reused for every optimizer's gradient
            // For triangular elements: ∇φ = Σ φ_i ∇N_i where N_i are shape functions
            VectorField forwardGradient = FiniteElementOperators.CalculateElementWiseGradient(_mesh, forwardSolution);

            // ========== STEP 3: EXTRACT ELECTRODE POTENTIALS ==========
            // Get simulated voltages at electrode positions from the FEM solution
            double[] electrodePotentials = _mesh.GetElectrodePotentials();

            // Clip unreasonable values (overflow, NaN) that could destabilize optimization
            // This is a safety measure for numerical robustness
            PotentialClipper.Clip(electrodePotentials);

            // ========== STEP 4: MEASUREMENT PROJECTION ==========
            // Project simulated and measured potentials into the comparison space
            // This handles:
            // - Filtering to active/adjacent electrodes only
            // - Converting to potential differences if configured
            // - Normalizing by drive amplitude
            // - Creating mapping functions for adjoint source expansion
            List<Electrode> electrodes = _mesh.GetElectrodes().ToList();

            var projection = MeasurementProjector.Create(electrodes,
                                                         _measurementSetup,
                                                         _usePotentialDifferences,
                                                         measurement,
                                                         electrodePotentials);

            // ========== STEP 5: ADJOINT SOLVES ==========
            // For each error metric, compute the adjoint field
            // The adjoint method allows us to compute gradients w.r.t. all parameters (element conductivities)
            // from just one adjoint solve per error metric (instead of N_elements forward solves!)
            // Results cached by error metric type to avoid duplicate computation
            var adjointSolutionsByBlock = EvaluateAdjointSolutions(projection);

            // ========== STEP 6: ADJOINT GRADIENTS ==========
            // Calculate ∇μ for each adjoint solution (needed for sensitivity computation)
            // Computed once per adjoint field, then reused across all optimizers
            var adjointGradientsByBlock = CalculateAdjointGradients(adjointSolutionsByBlock);

            // ========== STEP 7: REGULARIZATION ==========
            // Evaluate all regularizers on the current conductivity distribution
            // Regularizers penalize undesirable features (roughness, sparsity violations, etc.)
            // This stabilizes the ill-posed inverse problem
            var regularizerGradients = EvaluateRegularizers();

            // ========== STEP 8: OPTIMIZER-SPECIFIC GRADIENT ASSEMBLY ==========
            // Build gradients for each optimizer by following the canvas wiring:
            // - Find all ErrorMetric→Optimizer connections
            // - For each connection, compute ∂J/∂σ = -∇φ · ∇μ and weight by connection strength
            // - Sum contributions to get per-optimizer data misfit gradient
            var optimizerGradients = AssembleOptimizerGradients(forwardGradient, adjointGradientsByBlock);

            // ========== STEP 9: OPTIMIZER-SPECIFIC REGULARIZATION ==========
            // Build regularization terms for each optimizer by following Regularizer→Optimizer connections
            // Weight and sum regularizer gradients according to connection weights
            var optimizerRegularizations = AssembleOptimizerRegularizations(regularizerGradients);

            // ========== STEP 10: LEGACY AGGREGATION ==========
            // For legacy consumers that expect a single gradient/regularization pair, blend
            // the optimizer-specific outputs using the optimizer→model weights.
            // This maintains backward compatibility while supporting multi-optimizer architectures
            var combinedGradient = CombineOptimizerOutputs(optimizerGradients);
            var combinedRegularization = CombineOptimizerRegularizations(optimizerRegularizations);

            // ========== STEP 11: PACKAGE RESULTS ==========
            // Combine all components to form the reconstruction frame
            // This frame contains everything needed for:
            // - Optimization step (gradients, regularization)
            // - Visualization (forward/adjoint fields)
            // - Error analysis (measured vs simulated)
            // - Multi-optimizer support (per-optimizer gradients/regularizations)
            return new ReconstructionFrame(combinedGradient,
                                           forwardSolution,
                                           adjointSolutionsByBlock.Values.First(), // representative adjoint solution for visualization
                                           combinedRegularization,
                                           measurement,
                                           electrodePotentials,
                                           optimizerGradients,
                                           optimizerRegularizations);
        }

        /// <summary>
        /// Performs a forward FEM solve to compute the potential distribution.
        /// 
        /// MATHEMATICAL FORMULATION:
        /// Given:
        /// - Current conductivity σ(x,y) at each element
        /// - Boundary conditions (current injection at electrodes)
        /// 
        /// Solve the elliptic PDE (steady-state current flow):
        ///   ∇·(σ∇φ) = 0  in Ω (domain interior)
        ///   σ ∂φ/∂n = I  on Γ (electrode boundaries with current injection)
        ///   ∂φ/∂n = 0    on remaining boundary (insulated)
        /// 
        /// FEM discretization leads to linear system: K(σ)φ = f
        /// where K is the stiffness matrix, φ is node potentials, f is current sources
        /// 
        /// This is the "forward problem" - predicting measurements from known conductivity.
        /// </summary>
        /// <param name="boundaryCondition">Electrode states (current injection, ground, measurement)</param>
        /// <returns>Potential distribution φ(x,y) throughout the domain</returns>
        /// <exception cref="InvalidOperationException">If differential equation solver not initialized</exception>
        private PotentialDistribution ForwardSolve(FEMBoundaryCondition boundaryCondition)
        {
            if (_differentialEquationSolver == null)
            {
                throw new InvalidOperationException("The BlockReconstructionPersistence has not been initialized.");
            }

            // Solve the forward problem: returns potential at each mesh node
            // The solver internally assembles K(σ), applies boundary conditions, and solves the linear system
            return _differentialEquationSolver.Solve(_mesh, boundaryCondition, null);
        }

        /// <summary>
        /// Performs an adjoint FEM solve to compute sensitivity fields.
        /// 
        /// ADJOINT METHOD THEORY:
        /// Goal: Compute ∂J/∂σ where J is the objective function (error + regularization)
        /// 
        /// Direct approach would require N_params forward solves - infeasible for large meshes!
        /// 
        /// Adjoint method:
        /// 1. Define adjoint variable μ satisfying: K^T μ = ∂J/∂φ (adjoint equation)
        ///    where K is the forward operator, φ is the potential
        /// 
        /// 2. Then by chain rule and integration by parts:
        ///    ∂J/∂σ = -∫ ∇φ · ∇μ dΩ
        /// 
        /// Cost: 1 forward solve + 1 adjoint solve = 2 PDE solves total
        /// (vs. N_params forward solves for direct differentiation)
        /// 
        /// For EIT with thousands of elements, this is the difference between
        /// minutes and days of computation!
        /// </summary>
        /// <param name="adjointBoundaryCondition">Modified boundary conditions for adjoint problem</param>
        /// <param name="adjointSource">Right-hand side: ∂J/∂φ (sensitivity of objective to potentials)</param>
        /// <returns>Adjoint field μ(x,y) - sensitivity of objective to potential at each point</returns>
        /// <exception cref="InvalidOperationException">If differential equation solver not initialized</exception>
        private PotentialDistribution AdjointSolve(FEMBoundaryCondition adjointBoundaryCondition, double[] adjointSource)
        {
            if (_differentialEquationSolver == null)
            {
                throw new InvalidOperationException("The BlockReconstructionPersistence has not been initialized.");
            }

            // Convert adjoint source to Complex type (zero imaginary part)
            // This accommodates solvers that support frequency-domain analysis
            // For DC EIT, imaginary part is always zero
            Complex[] tmp = new Complex[adjointSource.Length];
            for (int i = 0; i < adjointSource.Length; i++)
                tmp[i] = new Complex(adjointSource[i], 0);

            // Solve the adjoint system: K^T μ = b where b is the adjoint source
            // In practice, since K is symmetric for DC EIT, K^T = K
            return _differentialEquationSolver.Solve(_mesh, adjointBoundaryCondition, tmp);
        }

        /// <summary>
        /// Evaluates the adjoint source for each error metric and computes corresponding adjoint fields.
        /// 
        /// DETAILED PROCESS:
        /// For each error metric E(φ_sim, φ_meas):
        /// 
        /// 1. Compute adjoint source b = ∂E/∂φ
        ///    This is the sensitivity of the error to changes in electrode potentials
        ///    Examples:
        ///    - L2 error: b_i = 2(φ_sim,i - φ_meas,i)
        ///    - Relative error: b_i = (φ_sim,i - φ_meas,i) / φ_meas,i²
        /// 
        /// 2. Expand adjoint source from measurement space to full electrode space
        ///    Projection may have filtered to active electrodes only - need to map back
        /// 
        /// 3. Set up adjoint boundary condition with these electrode "forces"
        /// 
        /// 4. Solve adjoint PDE: ∇·(σ∇μ) = b to get sensitivity field μ
        /// 
        /// 5. Cache by error metric TYPE (not ID) to avoid duplicate computation
        ///    Multiple blocks with same error metric type reuse the same adjoint solution
        /// </summary>
        /// <param name="projection">Projected measurement bundle with measured/simulated data and expansion maps</param>
        /// <returns>Dictionary mapping block ID to its adjoint solution</returns>
        private Dictionary<string, PotentialDistribution> EvaluateAdjointSolutions(MeasurementProjection projection)
        {
            var electrodes = _mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            // Reset electrode states for adjoint solves
            // Adjoint problem has different boundary conditions than forward problem
            foreach (var electrode in electrodes)
            {
                electrode.IsExcitation = false;  // No current injection
                electrode.IsGround = false;       // No ground
                electrode.IsMeasuring = true;     // All electrodes participate
            }

            // Cache adjoint solutions by error metric type to avoid duplicate evaluation
            // Multiple blocks of the same error metric type can share one adjoint solve
            var adjointCache = new Dictionary<Type, PotentialDistribution>();
            var solutions = new Dictionary<string, PotentialDistribution>();

            // Parallel evaluation for efficiency when multiple error metrics exist
            Parallel.ForEach(_errorMetricMap, kvp =>
            {
                var errorMetricType = kvp.Value.errorMetric.GetType();

                // Check cache first - if this metric type was already computed, reuse it
                PotentialDistribution adjointSolution;
                lock (adjointCache)
                {
                    if (adjointCache.TryGetValue(errorMetricType, out var cached))
                    {
                        lock (solutions)
                        {
                            solutions[kvp.Key] = cached;
                        }
                        return; // Skip computation, use cached result
                    }
                }

                // Compute adjoint source: ∂E/∂φ where E is the error metric
                // This tells us how sensitive the error is to changes in each electrode potential
                var adjointSource = kvp.Value.errorMetric.EvaluateAdjointSource(_mesh, projection.Measured, projection.Simulated);
                
                // Expand from measurement space (active electrodes) to full electrode space
                // This reverses the projection applied earlier
                var expandedAdjoint = projection.ExpandAdjoint(adjointSource);
                
                // Set up adjoint boundary condition with expanded source
                var adjointBoundaryCondition = new FEMBoundaryCondition(electrodes);
                adjointBoundaryCondition.SetElectrodePotentials(expandedAdjoint);
                
                // Solve adjoint PDE to get sensitivity field
                adjointSolution = AdjointSolve(adjointBoundaryCondition, adjointBoundaryCondition.GetElectrodePotentials());

                // Cache by type for reuse, and store by block ID for wiring
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

        /// <summary>
        /// Calculates spatial gradients (∇μ) of all adjoint fields.
        /// 
        /// For each adjoint solution μ(x,y), compute ∇μ = (∂μ/∂x, ∂μ/∂y) at each element.
        /// 
        /// These gradients are needed for the sensitivity computation:
        ///   ∂J/∂σ_e = -∫_Ω_e ∇φ · ∇μ dΩ
        /// 
        /// By computing all adjoint gradients once, we can efficiently assemble
        /// gradients for multiple optimizers that may use the same error metrics.
        /// </summary>
        /// <param name="adjointSolutionsByBlock">Adjoint fields indexed by block ID</param>
        /// <returns>Adjoint gradients (vector fields) indexed by block ID</returns>
        private Dictionary<string, VectorField> CalculateAdjointGradients(Dictionary<string, PotentialDistribution> adjointSolutionsByBlock)
        {
            var adjointGradients = new Dictionary<string, VectorField>();

            // Parallel computation for efficiency with multiple adjoint fields
            Parallel.ForEach(adjointSolutionsByBlock, kvp =>
            {
                // For triangular elements: ∇μ = Σ μ_i ∇N_i where N_i are shape functions
                // This gives constant gradient per element (piecewise linear approximation)
                var gradient = FiniteElementOperators.CalculateElementWiseGradient(_mesh, kvp.Value);
                lock (adjointGradients)
                {
                    adjointGradients[kvp.Key] = gradient;
                }
            });

            return adjointGradients;
        }

        /// <summary>
        /// Assembles optimizer-specific gradients by following the block canvas wiring.
        /// 
        /// WIRING ARCHITECTURE:
        /// The visual canvas defines explicit connections: ErrorMetric → Optimizer
        /// Each connection has a weight determining its contribution.
        /// 
        /// GRADIENT COMPUTATION:
        /// For each optimizer:
        ///   1. Find all connected error metrics (follow ErrorMetric → Optimizer connections)
        ///   2. For each connection:
        ///      a. Get the corresponding adjoint gradient ∇μ
        ///      b. Compute element-wise sensitivity: g_e = -(∇φ_e · ∇μ_e) * Area_e
        ///         This is the adjoint sensitivity formula from calculus of variations
        ///      c. Weight by: (solver→errorMetric weight) × (errorMetric→optimizer connection weight)
        ///      d. Accumulate weighted contribution
        ///   3. Return accumulated gradient for this optimizer
        /// 
        /// MATHEMATICAL INTERPRETATION:
        /// g_e = -∫_Ω_e ∇φ · ∇μ dΩ ≈ -(∇φ_e · ∇μ_e) * Area_e
        /// 
        /// This is the sensitivity of the objective function to conductivity σ_e in element e.
        /// Negative gradient points toward conductivity values that reduce the error.
        /// </summary>
        /// <param name="forwardGradient">∇φ (forward potential gradient) at each element</param>
        /// <param name="adjointGradientsByBlock">∇μ (adjoint gradients) indexed by error metric block ID</param>
        /// <returns>Per-optimizer gradient maps: optimizerId → ConductivityDistribution(elementId → sensitivity)</returns>
        private Dictionary<string, ConductivityDistribution> AssembleOptimizerGradients(VectorField forwardGradient,
                                                                                        Dictionary<string, VectorField> adjointGradientsByBlock)
        {
            var optimizerGradients = new Dictionary<string, ConductivityDistribution>();
            var elements = _mesh.GetElements().Cast<FEMElement>().ToList();

            // For each optimizer in the configuration
            foreach (var optimizer in _optimizerMap)
            {
                var optimizerId = optimizer.Key;
                
                // Find all ErrorMetric → Optimizer connections targeting this optimizer
                var connectedErrorMetrics = _connections?
                    .Where(c => c.TargetId == optimizerId && c.SourceType == BlockType.ErrorMetric)
                    .ToList() ?? new List<WeightedConnectionSnapshot>();

                // Accumulator for weighted gradient contributions
                var gradientAccumulator = new Dictionary<int, double>();

                // For each connected error metric
                foreach (var connection in connectedErrorMetrics)
                {
                    var errorMetricId = connection.SourceId;

                    // Get the adjoint gradient for this error metric
                    if (!adjointGradientsByBlock.TryGetValue(errorMetricId, out var adjointGradient))
                        continue;

                    // Compute gradient contribution for each element in parallel
                    Parallel.ForEach(elements, element =>
                    {
                        // Get forward gradient ∇φ at this element
                        var gradPhi = forwardGradient.GetVector(element.Id);

                        // Get adjoint gradient ∇μ at this element
                        var gradMu = adjointGradient.GetVector(element.Id);

                        // Compute dot product weighted by element area: -(∇φ · ∇μ) * A
                        // This approximates the integral ∫_Ω_e ∇φ · ∇μ dΩ
                        double dotProduct = -(gradPhi.X * gradMu.X + gradPhi.Y * gradMu.Y) * element.Area;

                        // Apply only the ErrorMetric→Optimizer weight in a dedicated helper.
                        double weighted = ApplyErrorMetricToOptimizerWeight(errorMetricId, optimizerId, dotProduct, connection.Weight);

                        // Accumulate into this optimizer's gradient
                        lock (gradientAccumulator)
                        {
                            if (gradientAccumulator.ContainsKey(element.Id))
                                gradientAccumulator[element.Id] += weighted;
                            else
                                gradientAccumulator[element.Id] = weighted;
                        }
                    });
                }

                // Store this optimizer's assembled gradient
                optimizerGradients[optimizerId] = new ConductivityDistribution(gradientAccumulator);
            }

            return optimizerGradients;
        }

        /// <summary>
        /// Evaluates all configured regularizers on the current conductivity distribution.
        /// 
        /// REGULARIZATION PURPOSE:
        /// EIT inverse problem is severely ill-posed (many conductivity distributions can produce similar measurements).
        /// Regularization adds prior knowledge to prefer "reasonable" solutions:
        /// - Smoothness (Tikhonov): prefer smooth conductivity variations
        /// - Sparsity (L1): prefer piecewise constant distributions
        /// - Total Variation (TV): prefer sharp boundaries with smooth regions
        /// - Prior-based: penalize deviation from known background
        /// 
        /// MATHEMATICAL FORM:
        /// Minimize: J(σ) = E(σ) + λ R(σ)
        /// where E is data misfit, R is regularization, λ is regularization parameter
        /// 
        /// Gradient: ∂J/∂σ = ∂E/∂σ + λ ∂R/∂σ
        /// 
        /// This method computes ∂R/∂σ for each regularizer, weighted by solver→regularizer connection.
        /// </summary>
        /// <returns>Regularization gradients indexed by regularizer block ID</returns>
        private Dictionary<string, ConductivityDistribution> EvaluateRegularizers()
        {
            // Get current conductivity estimate (the distribution we're regularizing)
            var currentDistribution = _mesh?.GetConductivityDistribution() ?? _initialDistribution
                                       ?? throw new InvalidOperationException("No conductivity distribution available for regularization evaluation.");

            var regularizations = new Dictionary<string, ConductivityDistribution>();

            // Evaluate each regularizer in parallel
            Parallel.ForEach(_regularizerMap, kvp =>
            {
                // Compute ∂R/∂σ for this regularizer. Regularizer→Optimizer weights are applied later
                // during assembly to avoid scaling more than once.
                var weighted = kvp.Value.regulizer.EvaluateGradient(_mesh, currentDistribution);

                lock (regularizations)
                {
                    regularizations[kvp.Key] = weighted;
                }
            });

            return regularizations;
        }

        /// <summary>
        /// Assembles optimizer-specific regularization contributions by following canvas wiring.
        /// 
        /// WIRING ARCHITECTURE:
        /// The visual canvas defines: Regularizer → Optimizer connections
        /// Each connection has a weight determining the regularization strength.
        /// 
        /// ASSEMBLY PROCESS:
        /// For each optimizer:
        ///   1. Find all connected regularizers (follow Regularizer → Optimizer connections)
        ///   2. For each connection:
        ///      a. Get the regularizer gradient (already weighted by solver→regularizer)
        ///      b. Weight by regularizer→optimizer connection weight
        ///      c. Accumulate weighted contribution
        ///   3. Return accumulated regularization for this optimizer
        /// 
        /// This allows different optimizers to use different regularization strategies:
        /// - Optimizer A might use strong smoothness + weak sparsity
        /// - Optimizer B might use strong sparsity only
        /// - Their outputs are later blended based on optimizer→model weights
        /// </summary>
        /// <param name="regularizerGradients">Pre-computed regularization gradients indexed by regularizer block ID</param>
        /// <returns>Per-optimizer regularization maps: optimizerId → ConductivityDistribution(elementId → regularization gradient)</returns>
        private Dictionary<string, ConductivityDistribution> AssembleOptimizerRegularizations(Dictionary<string, ConductivityDistribution> regularizerGradients)
        {
            var optimizerRegularizations = new Dictionary<string, ConductivityDistribution>();

            // For each configured optimizer
            foreach (var optimizer in _optimizerMap)
            {
                var optimizerId = optimizer.Key;
                
                // Find all Regularizer → Optimizer connections targeting this optimizer
                var connectedRegularizers = _connections?
                    .Where(c => c.TargetId == optimizerId && c.SourceType == BlockType.Regularizer)
                    .ToList() ?? new List<WeightedConnectionSnapshot>();

                // Accumulator for weighted regularization contributions
                var accumulator = new Dictionary<int, double>();

                // For each connected regularizer
                foreach (var connection in connectedRegularizers)
                {
                    var regId = connection.SourceId;

                    // Get the pre-computed regularization gradient (unweighted; connection scaling applied below)
                    if (!regularizerGradients.TryGetValue(regId, out var reg))
                        continue;

                    // Weight by regularizer→optimizer connection and accumulate
                    foreach (var kvp in reg.IdValuePairs)
                    {
                        double weighted = ApplyRegularizerToOptimizerWeight(regId, optimizerId, kvp.Value, connection.Weight);

                        if (accumulator.ContainsKey(kvp.Key))
                            accumulator[kvp.Key] += weighted;
                        else
                            accumulator[kvp.Key] = weighted;
                    }
                }

                // Store this optimizer's assembled regularization gradient
                optimizerRegularizations[optimizerId] = new ConductivityDistribution(accumulator);
            }

            return optimizerRegularizations;
        }

        /// <summary>
        /// Combines multiple optimizer-specific gradients into a single legacy gradient.
        /// 
        /// BLENDING STRATEGY:
        /// When multiple optimizers are configured, they may produce different gradient estimates.
        /// This method blends them using their optimizer→model connection weights:
        /// 
        /// g_combined = Σ(w_i * g_i) / Σ(w_i)
        /// 
        /// where w_i is the weight of optimizer i, g_i is its gradient.
        /// 
        /// PURPOSE:
        /// - Maintains backward compatibility with single-optimizer consumers
        /// - Provides a single "consensus" gradient for visualization
        /// - Actual optimization may use per-optimizer gradients for better control
        /// </summary>
        /// <param name="optimizerGradients">Per-optimizer gradients from AssembleOptimizerGradients</param>
        /// <returns>Weighted average of all optimizer gradients</returns>
        private ConductivityDistribution CombineOptimizerOutputs(IReadOnlyDictionary<string, ConductivityDistribution> optimizerGradients)
        {
            var combined = new Dictionary<int, double>();
            double totalWeight = 0.0;

            // Accumulate weighted gradients
            foreach (var kvp in optimizerGradients)
            {
                var weight = GetOptimizerToModelWeight(kvp.Key);
                totalWeight += weight;

                // Add weighted contribution from this optimizer
                foreach (var value in kvp.Value.IdValuePairs)
                {
                    if (combined.ContainsKey(value.Key))
                        combined[value.Key] += weight * value.Value;
                    else
                        combined[value.Key] = weight * value.Value;
                }
            }

            // Normalize by total weight to get weighted average
            if (totalWeight > 0)
            {
                foreach (var id in combined.Keys.ToList())
                    combined[id] /= totalWeight;
            }

            return new ConductivityDistribution(combined);
        }

        /// <summary>
        /// Combines multiple optimizer-specific regularizations into a single legacy regularization gradient.
        /// 
        /// Similar to CombineOptimizerOutputs but for regularization terms.
        /// Blends using optimizer→model weights to produce a single aggregated regularization.
        /// 
        /// This allows legacy consumers to work with a single regularization term
        /// while the system internally supports per-optimizer regularization strategies.
        /// </summary>
        /// <param name="optimizerRegularizations">Per-optimizer regularizations from AssembleOptimizerRegularizations</param>
        /// <returns>Weighted average of all optimizer regularization gradients</returns>
        private ConductivityDistribution CombineOptimizerRegularizations(IReadOnlyDictionary<string, ConductivityDistribution> optimizerRegularizations)
        {
            var combined = new Dictionary<int, double>();
            double totalWeight = 0.0;

            // Accumulate weighted regularizations
            foreach (var kvp in optimizerRegularizations)
            {
                var weight = GetOptimizerToModelWeight(kvp.Key);
                totalWeight += weight;

                // Add weighted contribution from this optimizer's regularization
                foreach (var value in kvp.Value.IdValuePairs)
                {
                    if (combined.ContainsKey(value.Key))
                        combined[value.Key] += weight * value.Value;
                    else
                        combined[value.Key] = weight * value.Value;
                }
            }

            // Normalize by total weight to get weighted average
            if (totalWeight > 0)
            {
                foreach (var id in combined.Keys.ToList())
                    combined[id] /= totalWeight;
            }

            return new ConductivityDistribution(combined);
        }

        /// <summary>
        /// Applies the configured ErrorMetric→Optimizer weight to a raw gradient contribution.
        /// </summary>
        private double ApplyErrorMetricToOptimizerWeight(string errorMetricId, string optimizerId, double value, double fallbackWeight = 1.0)
        {
            double weight = GetConnectionWeight(errorMetricId, optimizerId, BlockType.ErrorMetric, BlockType.Optimizer, fallbackWeight);
            return weight * value;
        }

        /// <summary>
        /// Applies the configured Regularizer→Optimizer weight to a regularization term.
        /// </summary>
        private double ApplyRegularizerToOptimizerWeight(string regularizerId, string optimizerId, double value, double fallbackWeight = 1.0)
        {
            double weight = GetConnectionWeight(regularizerId, optimizerId, BlockType.Regularizer, BlockType.Optimizer, fallbackWeight);
            return weight * value;
        }

        /// <summary>
        /// Retrieves the Optimizer→Model weight for the given optimizer block.
        /// </summary>
        private double GetOptimizerToModelWeight(string optimizerId)
        {
            var connectionWeight = _connections?
                                        .FirstOrDefault(c => c.SourceId == optimizerId &&
                                                             c.SourceType == BlockType.Optimizer &&
                                                             c.TargetType == BlockType.Model)?.Weight;

            if (connectionWeight.HasValue)
                return connectionWeight.Value;

            return _optimizerMap.TryGetValue(optimizerId, out var optimizerDescriptor)
                ? optimizerDescriptor.weight
                : 1.0;
        }

        /// <summary>
        /// Looks up a connection weight between two block ids. Defaults to 1.0 when absent to avoid
        /// accidental double-scaling and to keep legacy configurations functional.
        /// </summary>
        private double GetConnectionWeight(string sourceId, string targetId, BlockType sourceType, BlockType targetType, double fallbackWeight = 1.0)
        {
            return _connections?
                       .FirstOrDefault(c => c.SourceId == sourceId &&
                                            c.TargetId == targetId &&
                                            c.SourceType == sourceType &&
                                            c.TargetType == targetType)?.Weight
                   ?? fallbackWeight;
        }

        /// <summary>
        /// Normalizes a step index to wrap within the cycle length.
        /// 
        /// EIT drive patterns are cyclic (e.g., 16 steps for 16 electrodes in adjacent pattern).
        /// This ensures step indices always map to valid pattern steps even when
        /// the reconstruction runs for multiple cycles.
        /// 
        /// Handles negative indices correctly (wraps to positive).
        /// </summary>
        /// <param name="stepIndex">Raw step index (may be negative or > cycleLength)</param>
        /// <param name="cycleLength">Number of steps in one complete pattern cycle</param>
        /// <returns>Normalized index in range [0, cycleLength-1]</returns>
        private static int NormalizeStepIndex(int stepIndex, int cycleLength)
        {
            int normalized = stepIndex % cycleLength;
            return normalized < 0 ? normalized + cycleLength : normalized;
        }

        /// <summary>
        /// Normalizes an electrode index to wrap within the electrode count.
        /// 
        /// Ensures electrode indices are always valid even when pattern logic
        /// generates indices outside [0, electrodeCount-1] (e.g., adjacent pairs wrapping around).
        /// 
        /// Handles negative indices correctly (wraps to positive).
        /// </summary>
        /// <param name="index">Raw electrode index (may be negative or >= electrodeCount)</param>
        /// <param name="electrodeCount">Total number of electrodes</param>
        /// <returns>Normalized index in range [0, electrodeCount-1]</returns>
        private static int NormalizeElectrodeIndex(int index, int electrodeCount)
        {
            int normalized = index % electrodeCount;
            return normalized < 0 ? normalized + electrodeCount : normalized;
        }
    }
}
