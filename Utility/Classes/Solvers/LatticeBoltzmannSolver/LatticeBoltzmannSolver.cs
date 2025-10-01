using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// GPU-accelerated Lattice Boltzmann Method solver for diffusion PDEs in electrical impedance tomography.
    /// Solves ∇·(γ∇φ)=f using D2Q9 lattice with BGK collision, streaming, and bounce-back boundaries.
    /// Supports both CPU and CUDA execution paths for maximum compatibility and performance.
    /// </summary>
    public sealed class LatticeBoltzmannSolver : ISolver
    {
        // Solver configuration parameters
        private int MaxIterationCount = 250;                    // Maximum time steps before forced termination
        private double SolutionTolerance = 1e-6;               // Convergence tolerance for steady-state detection
        private int ConvergenceCheckFrequency = 100;           // How often to check convergence (computational cost)
        private readonly bool _useCuda;                        // Whether to use GPU acceleration

        // LBM stability constants
        private const double TauSafetyEpsilon = 1e-6;          // Small value to prevent numerical instability
        private const double MinTau = 0.5 + TauSafetyEpsilon; // Minimum relaxation time for stability

        // CUDA kernel management - static to share across solver instances
        private static readonly object _cudaKernelLock = new(); // Thread-safe kernel compilation
        
        // Pre-compiled CUDA kernels for LBM operations
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _initializeKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<double>, double, double, ArrayView<double>>? _collisionKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>>? _streamKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _updateKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>>? _phiKernel;

        /// <summary>
        /// Initializes LBM solver with specified convergence criteria and execution mode.
        /// </summary>
        /// <param name="maxIterationCount">Maximum time steps before termination</param>
        /// <param name="solutionTolerance">Relative change threshold for convergence</param>
        /// <param name="convergenceCheckFrequency">Iteration interval for convergence testing</param>
        /// <param name="useCuda">Enable GPU acceleration if available</param>
        public LatticeBoltzmannSolver(int maxIterationCount, double solutionTolerance, int convergenceCheckFrequency, bool useCuda = false)
        {
            MaxIterationCount = maxIterationCount;
            SolutionTolerance = solutionTolerance;
            ConvergenceCheckFrequency = convergenceCheckFrequency;
            _useCuda = useCuda;
        }

        /// <summary>
        /// Validates and clamps conductivity values to prevent numerical issues.
        /// Invalid values (NaN, infinity) are set to zero for stability.
        /// </summary>
        private static double SanitizeConductivity(double conductivity)
        {
            // Check for invalid floating-point values that could crash GPU kernels
            if (double.IsNaN(conductivity) || double.IsInfinity(conductivity))
                return 0.0;

            // Ensure non-negative conductivity (physical constraint)
            return Math.Max(0.0, conductivity);
        }

        /// <summary>
        /// Computes BGK relaxation time from material conductivity.
        /// Relates physical diffusion coefficient to LBM collision frequency.
        /// </summary>
        /// <param name="conductivity">Material electrical conductivity</param>
        /// <param name="csSquared">Lattice speed of sound squared</param>
        /// <returns>Relaxation time ensuring numerical stability</returns>
        private static double ComputeRelaxationTime(double conductivity, double csSquared)
        {
            // Standard LBM relationship: τ = D/cs² + 0.5, where D is diffusion coefficient
            double tau = conductivity / csSquared + 0.5;
            
            // Enforce minimum relaxation time to prevent numerical instability
            // Values below 0.5 can cause negative distribution functions
            return tau < MinTau ? MinTau : tau;
        }

        /// <summary>
        /// Main entry point for forward problem solving with automatic CPU/GPU selection.
        /// </summary>
        public PotentialDistribution SolveForward(IDiscretization discretization, BoundaryCondition boundaryCondition)
        {
            // Cast to LBM-specific types (throw if incompatible)
            var lbmGrid = discretization as LBMGrid ?? throw new InvalidCastException();
            var bc = boundaryCondition as LBMBoundaryCondition ?? throw new InvalidCastException();

            // Route to appropriate implementation based on configuration
            return _useCuda ? RunForwardCuda(lbmGrid, bc) : RunForward(lbmGrid, bc);
        }

        /// <summary>
        /// Solves adjoint problem by reusing forward solver with modified boundary conditions.
        /// Adjoint method enables efficient gradient computation for inverse problems.
        /// </summary>
        public PotentialDistribution SolveAdjoint(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[] adjointSource)
        {
            var lbmGrid = discretization as LBMGrid ?? throw new InvalidCastException();
            var bc = boundaryCondition as LBMBoundaryCondition ?? throw new InvalidCastException();
            
            // Get electrode collections for modification
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().ToList();
            int bcElectrodeCount = bcElectrodes.Count();

            // Set up adjoint boundary conditions: zero potential, adjoint current sources
            for(int i = 0; i < bcElectrodeCount; i++)
            {
                bcElectrodes[i].Potential = 0.0;        // Homogeneous Dirichlet conditions
                electrodes[i].Potential = 0.0;

                bcElectrodes[i].Current = adjointSource[i].Real;   // TODO: add complex currents
                electrodes[i].Current = adjointSource[i].Real;
            }

            // Solve modified forward problem (adjoint equation has same structure)
            return _useCuda ? RunForwardCuda(lbmGrid, bc) : RunForward(lbmGrid, bc);
        }

        // Direct CUDA interface methods for explicit GPU usage
        public PotentialDistribution CUDASolveForward(IDiscretization discretization, BoundaryCondition boundaryCondition)
        {
            var lbmGrid = discretization as LBMGrid ?? throw new InvalidCastException();
            var bc = boundaryCondition as LBMBoundaryCondition ?? throw new InvalidCastException();
            return RunForwardCuda(lbmGrid, bc);
        }

        public PotentialDistribution CUDASolveAdjoint(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[] adjointSource)
        {
            var lbmGrid = discretization as LBMGrid ?? throw new InvalidCastException();
            var bc = boundaryCondition as LBMBoundaryCondition ?? throw new InvalidCastException();
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().ToList();
            int bcElectrodeCount = bcElectrodes.Count();

            for (int i = 0; i < bcElectrodeCount; i++)
            {
                bcElectrodes[i].Potential = 0.0;
                electrodes[i].Potential = 0.0;
                bcElectrodes[i].Current = adjointSource[i].Real;
                electrodes[i].Current = adjointSource[i].Real;
            }

            return RunForwardCuda(lbmGrid, bc);
        }

        /// <summary>
        /// CPU implementation of LBM forward solver using traditional object-oriented approach.
        /// Serves as reference implementation and fallback when GPU is unavailable.
        /// </summary>
        private PotentialDistribution RunForward(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            // Copy solver parameters to local variables for performance
            int maxIter = MaxIterationCount;
            double tol = SolutionTolerance;
            int checkFreq = ConvergenceCheckFrequency;

            // Get LBM constants for calculations
            var weights = LatticeBoltzmannConstants.Weights;     // Equilibrium distribution weights
            var opposite = LatticeBoltzmannConstants.Opposite;   // Opposite direction mapping
            double csSquared = LatticeBoltzmannConstants.CsSquared; // Lattice speed of sound squared

            // Extract mesh data for processing
            var elements = lbmGrid.GetElements().Cast<LBMElement>().ToList();
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().ToList();

            // PHASE 1: Initialize distribution functions with boundary conditions
            foreach (var el in elements)
            {
                bool isWall = el.IsWall;

                // Initialize all 9 distribution functions
                for (int k = 0; k < 9; k++)
                {
                    el.Fi_next[k] = 0.0;                         // Clear temporary storage
                    el.Fi[k] = isWall ? 0.0 : weights[k];        // Equilibrium with φ=1 for fluid
                }

                // Skip further processing for wall cells
                if (isWall)
                    continue;

                // Apply electrode boundary conditions
                if(el.IsElectrode)
                {
                    var correspondingElectrode = electrodes.Find(x => x.GridId == el.Id);

                    if (correspondingElectrode != null)
                    {
                        // Neumann boundary condition: prescribed current (excitation/ground electrodes)
                        if(correspondingElectrode.IsExcitation || correspondingElectrode.IsGround)
                        {
                            // Set distribution functions proportional to current
                            double current = correspondingElectrode.Current;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = weights[i] * current;

                            // Implement bounce-back for directions hitting walls
                            var neighbors = el.Neighbors;
                            for (int i = 0; i < 9; i++)
                            {
                                if (neighbors[i].IsWall)
                                {
                                    // Bounce particle back to opposite direction
                                    el.Fi[opposite[i]] += el.Fi[i];
                                    el.Fi[i] = 0.0;
                                }
                            }
                        }
                        else // Dirichlet boundary condition: prescribed potential
                        {
                            correspondingElectrode.Potential = bcElectrodes[correspondingElectrode.Id].Potential;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = weights[i] * correspondingElectrode.Potential;
                        }
                    }
                }
            }

            // PHASE 2: Load material properties into elements
            var sigmaDist = lbmGrid.GetConductivityDistribution();
            foreach (var el in elements)
            {
                if (el.IsWall)
                {
                    el.Conductivity = 0.0; // Walls have zero conductivity
                    continue;
                }

                // Load and validate conductivity from distribution
                el.Conductivity = SanitizeConductivity(sigmaDist.GetConductivity(el.Id));
            }

            // PHASE 3: Mark electrode cells for boundary condition enforcement
            foreach (var electrode in bcElectrodes)
            {
                var cell = elements.First(e => e.Id == electrode.GridId);
                if (cell != null)
                    cell.IsElectrode = true;
            }

            // PHASE 4: Main LBM time-stepping loop
            double[] prevPhi = new double[elements.Count()]; // Previous iteration for convergence check
            
            for (int t = 0; t < maxIter; t++)
            {
                // STEP 4a: BGK Collision - relax distributions toward local equilibrium
                foreach (var el in elements)
                {
                    if (el.IsWall)
                        continue;

                    // Compute macroscopic potential (zeroth moment of distribution)
                    double phi = 0;
                    for (int k = 0; k < 9; k++)
                        phi += el.Fi[k];

                    // Calculate relaxation parameters
                    double tau = ComputeRelaxationTime(el.Conductivity, csSquared);
                    double omega = 1.0 / tau; // Collision frequency

                    // Apply BGK collision: Fi = Fi - ω(Fi - Feq)
                    for (int k = 0; k < 9; k++)
                    {
                        double geq = weights[k] * phi; // Local equilibrium distribution
                        el.Fi[k] += -omega * (el.Fi[k] - geq); // Relax toward equilibrium
                    }
                }

                // STEP 4b: Streaming - propagate distributions to neighboring cells
                foreach (var el in elements)
                {
                    if (el.IsWall)
                        continue;

                    for (int k = 0; k < 9; k++)
                    {
                        var nb = el.Neighbors[k]; // Neighbor in direction k

                        if (!nb.IsWall)
                        {
                            // Normal streaming: send to same direction slot in neighbor
                            nb.Fi_next[k] = el.Fi[k];
                        }
                        else
                        {
                            // Bounce-back: reflect off wall to opposite direction
                            el.Fi_next[opposite[k]] = el.Fi[k];
                        }
                    }
                }

                // STEP 4c: Update distributions and enforce boundary conditions
                foreach (var el in elements)
                {
                    if (el.IsWall)
                        continue;

                    // Copy streamed values from temporary to current arrays
                    for (int k = 0; k < 9; k++)
                    {
                        el.Fi[k] = el.Fi_next[k];
                        el.Fi_next[k] = 0.0; // Clear for next iteration
                    }
                    
                    // Re-enforce electrode boundary conditions after streaming
                    if (el.IsElectrode)
                    {
                        var electrode = electrodes.Find(x => x.GridId == el.Id) ?? throw new ArgumentNullException("Cannot find electrode with specified id!");
                        
                        if(electrode.IsExcitation || electrode.IsGround)
                        {
                            // Neumann: reset distributions to current value
                            double current = electrode.Current;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = weights[i] * current;

                            // Apply bounce-back for wall neighbors
                            var neighbors = el.Neighbors;
                            for(int i = 0; i < 9; i++)
                            {
                                if (neighbors[i].IsWall)
                                {
                                    el.Fi[opposite[i]] += el.Fi[i];
                                    el.Fi[i] = 0.0;
                                }
                            }
                        }
                        else
                        {
                            // Dirichlet: reset distributions to potential value
                            double potential = electrode.Potential;
                            for (int k = 0; k < 9; k++)
                                el.Fi[k] = weights[k] * potential;
                        }
                    }
                }

                // STEP 4d: Check convergence periodically to save computation
                if (t % checkFreq == 0)
                {
                    // Compute current potential field
                    var phi = elements.Select(e => e.Fi.Sum()).ToArray();

                    // Calculate relative change from previous check
                    double num = 0, den = 0;
                    for (int i = 0; i < phi.Length; i++)
                    {
                        double d = phi[i] - prevPhi[i];
                        num += d * d;       // Numerator: sum of squared differences
                        den += phi[i] * phi[i]; // Denominator: sum of squared values
                    }

                    // Check relative convergence criterion
                    if (den > 0 && Math.Sqrt(num / den) < tol)
                        break; // Converged - exit time loop early
                        
                    Array.Copy(phi, prevPhi, phi.Length); // Store for next check
                }
            }

            // PHASE 5: Extract final solution and update mesh state
            var dict = new Dictionary<int, double>();
            foreach (var element in elements)
                dict.Add(element.Id, element.Fi.Sum()); // Potential = sum of distributions

            var pd = new PotentialDistribution(dict);
            lbmGrid.SetPotentialDistribution(pd); // Update mesh with solution
            return pd;
        }

        /// <summary>
        /// GPU-accelerated implementation of LBM forward solver using CUDA kernels.
        /// Processes entire mesh in parallel for maximum performance on large grids.
        /// </summary>
        private PotentialDistribution RunForwardCuda(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            // Ensure CUDA kernels are compiled and ready
            EnsureCudaKernels();

            // Copy solver parameters for kernel execution
            int maxIter = MaxIterationCount;
            double tol = SolutionTolerance;
            int checkFreq = ConvergenceCheckFrequency;

            // Convert mesh to flat arrays optimized for GPU access
            var topology = LatticeBoltzmannCudaHelper.BuildTopology(lbmGrid);
            int elementCount = topology.ElementCount;

            // Handle empty mesh case
            if (elementCount == 0)
            {
                var empty = new PotentialDistribution(new Dictionary<int, double>());
                lbmGrid.SetPotentialDistribution(empty);
                return empty;
            }

            // Extract flattened mesh data for GPU processing
            var elements = topology.Elements;           // Original element objects
            var elementIds = topology.ElementIds;       // Linear ID mapping
            
            // Get electrode data for boundary condition setup
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToArray();
            var bcElectrodes = bc.GetElectrodes().Cast<LBMElectrode>().ToArray();

            // Create fast lookup dictionaries for electrode properties
            var electrodeByGridId = electrodes.ToDictionary(e => e.GridId);
            var bcElectrodeById = bcElectrodes.ToDictionary(e => e.Id);
            var bcElectrodeByGridId = bcElectrodes.ToDictionary(e => e.GridId);

            // Extract pre-computed topology arrays
            var isWallHost = topology.IsWall;               // Wall flags for each element
            var neighborIndicesHost = topology.NeighborIndices;  // Flattened neighbor connectivity
            var neighborIsWallHost = topology.NeighborIsWall;    // Neighbor wall flags

            // Prepare host arrays for GPU transfer
            var isElectrodeHost = new int[elementCount];        // Electrode identification flags
            var electrodeIsSourceHost = new int[elementCount];   // Source (Neumann) vs sink (Dirichlet) flags
            var electrodeCurrentHost = new double[elementCount]; // Current values for source electrodes
            var electrodePotentialHost = new double[elementCount]; // Potential values for sink electrodes
            var conductivityHost = new double[elementCount];     // Material conductivity per element

            // Get conductivity distribution from mesh
            var sigmaDist = lbmGrid.GetConductivityDistribution();

            // Process each element to extract material and boundary properties
            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                
                // Load and sanitize conductivity
                double conductivity = SanitizeConductivity(sigmaDist.GetConductivity(element.Id));
                element.Conductivity = conductivity;
                conductivityHost[idx] = conductivity;

                // Initialize electrode properties
                bool isElectrode = element.IsElectrode;
                double electrodeCurrent = 0.0;
                double electrodePotential = 0.0;
                int isSource = 0; // 0=Dirichlet (potential), 1=Neumann (current)

                // Check if element is an electrode in the main grid
                if (electrodeByGridId.TryGetValue(element.Id, out var electrode))
                {
                    isElectrode = true;
                    electrodeCurrent = electrode.Current;
                    
                    // Determine boundary condition type
                    if (electrode.IsExcitation || electrode.IsGround)
                    {
                        isSource = 1; // Neumann boundary condition
                    }
                    else
                    {
                        // Dirichlet boundary condition - find potential value
                        if (bcElectrodeById.TryGetValue(electrode.Id, out var bcElectrode))
                            electrodePotential = bcElectrode.Potential;
                        else if (bcElectrodeByGridId.TryGetValue(element.Id, out var bcElectrodeByGrid))
                            electrodePotential = bcElectrodeByGrid.Potential;
                        else
                            electrodePotential = electrode.Potential;
                    }
                }
                // Check if element is electrode in boundary condition only
                else if (bcElectrodeByGridId.TryGetValue(element.Id, out var bcElectrode))
                {
                    isElectrode = true;
                    electrodePotential = bcElectrode.Potential; // Always Dirichlet for BC-only electrodes
                }

                // Update element and store in host arrays
                element.IsElectrode = isElectrode;
                isElectrodeHost[idx] = isElectrode ? 1 : 0;
                electrodeIsSourceHost[idx] = isSource;
                electrodeCurrentHost[idx] = electrodeCurrent;
                electrodePotentialHost[idx] = electrodePotential;
            }

            // Final conductivity sanitization pass
            for (int idx = 0; idx < elementCount; idx++)
            {
                conductivityHost[idx] = SanitizeConductivity(conductivityHost[idx]);
            }

            // Get GPU accelerator and allocate device memory
            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            // Allocate GPU memory buffers for LBM data
            using var fiBuffer = accelerator.Allocate1D<double>(elementCount * 9);        // Distribution functions
            using var fiNextBuffer = accelerator.Allocate1D<double>(elementCount * 9);   // Temporary for streaming
            using var conductivityBuffer = accelerator.Allocate1D<double>(elementCount);  // Material properties
            using var isWallBuffer = accelerator.Allocate1D<int>(elementCount);          // Wall identification
            using var isElectrodeBuffer = accelerator.Allocate1D<int>(elementCount);     // Electrode identification
            using var electrodeIsSourceBuffer = accelerator.Allocate1D<int>(elementCount); // Boundary type flags
            using var electrodeCurrentBuffer = accelerator.Allocate1D<double>(elementCount); // Current values
            using var electrodePotentialBuffer = accelerator.Allocate1D<double>(elementCount); // Potential values
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(elementCount * 9);     // Neighbor connectivity
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(elementCount * 9);   // Neighbor wall flags
            using var phiBuffer = accelerator.Allocate1D<double>(elementCount);          // Potential field for convergence

            // Copy host data to GPU memory
            isWallBuffer.CopyFromCPU(isWallHost);
            isElectrodeBuffer.CopyFromCPU(isElectrodeHost);
            electrodeIsSourceBuffer.CopyFromCPU(electrodeIsSourceHost);
            electrodeCurrentBuffer.CopyFromCPU(electrodeCurrentHost);
            electrodePotentialBuffer.CopyFromCPU(electrodePotentialHost);
            conductivityBuffer.CopyFromCPU(conductivityHost);
            neighborIndexBuffer.CopyFromCPU(neighborIndicesHost);
            neighborIsWallBuffer.CopyFromCPU(neighborIsWallHost);

            // Initialize distribution functions and boundary conditions on GPU
            if (_initializeKernel == null)
                throw new NullReferenceException();

            _initializeKernel(elementCount,              // Number of elements to process
                fiBuffer.View,                           // Distribution functions output
                fiNextBuffer.View,                       // Temporary array (zeroed)
                isWallBuffer.View,                       // Wall identification
                isElectrodeBuffer.View,                  // Electrode identification
                electrodeIsSourceBuffer.View,            // Boundary condition types
                electrodeCurrentBuffer.View,             // Current values for Neumann BCs
                electrodePotentialBuffer.View,           // Potential values for Dirichlet BCs
                neighborIsWallBuffer.View,               // Neighbor wall flags for bounce-back
                LatticeBoltzmannCudaContext.OppositeView, // Opposite direction mapping
                LatticeBoltzmannCudaContext.WeightsView); // D2Q9 equilibrium weights

            accelerator.Synchronize(); // Wait for initialization to complete

            // Allocate host array for convergence checking
            double[] prevPhi = new double[elementCount];

            // Verify all kernels are compiled
            if (_collisionKernel == null || _streamKernel == null || _updateKernel == null || _phiKernel == null)
                throw new NullReferenceException();

            // Main GPU time-stepping loop
            for (int t = 0; t < maxIter; t++)
            {
                // Execute collision kernel: BGK relaxation toward equilibrium
                _collisionKernel(elementCount,                    // Number of elements to process
                    fiBuffer.View,                                // Distribution functions (input/output)
                    isWallBuffer.View,                           // Wall identification
                    conductivityBuffer.View,                     // Material conductivity
                    LatticeBoltzmannConstants.CsSquared,         // Lattice speed of sound squared
                    MinTau,                                      // Minimum relaxation time for stability
                    LatticeBoltzmannCudaContext.WeightsView);    // D2Q9 equilibrium weights

                // Execute streaming kernel: propagate distributions to neighbors
                _streamKernel(elementCount,                       // Number of elements to process
                    fiBuffer.View,                               // Source distribution functions
                    fiNextBuffer.View,                           // Destination distribution functions
                    isWallBuffer.View,                           // Wall identification
                    neighborIndexBuffer.View,                    // Neighbor connectivity
                    neighborIsWallBuffer.View,                   // Neighbor wall flags
                    LatticeBoltzmannCudaContext.OppositeView);   // Opposite directions for bounce-back

                // Execute update kernel: copy streamed values and enforce boundary conditions
                _updateKernel(elementCount,                       // Number of elements to process
                    fiBuffer.View,                               // Distribution functions (output)
                    fiNextBuffer.View,                           // Streamed values (input)
                    isWallBuffer.View,                           // Wall identification
                    isElectrodeBuffer.View,                      // Electrode identification
                    electrodeIsSourceBuffer.View,                // Boundary condition types
                    electrodeCurrentBuffer.View,                 // Current values for Neumann BCs
                    electrodePotentialBuffer.View,               // Potential values for Dirichlet BCs
                    neighborIsWallBuffer.View,                   // Neighbor wall flags
                    LatticeBoltzmannCudaContext.OppositeView,    // Opposite directions
                    LatticeBoltzmannCudaContext.WeightsView);    // D2Q9 equilibrium weights

                // Check convergence periodically
                if (t % checkFreq == 0)
                {
                    accelerator.Synchronize(); // Ensure all kernels complete
                    
                    // Compute potential field (sum of distributions) on GPU
                    _phiKernel(elementCount, fiBuffer.View, phiBuffer.View);
                    accelerator.Synchronize(); // Wait for computation
                    
                    // Copy potential field back to CPU for convergence check
                    var phiHost = phiBuffer.GetAsArray1D();

                    // Calculate relative change from previous iteration
                    double num = 0.0;
                    double den = 0.0;
                    for (int i = 0; i < phiHost.Length; i++)
                    {
                        double diff = phiHost[i] - prevPhi[i];
                        num += diff * diff;           // Sum of squared differences
                        den += phiHost[i] * phiHost[i]; // Sum of squared values
                    }

                    // Check convergence criterion
                    if (den > 0.0 && Math.Sqrt(num / den) < tol)
                        break; // Converged - exit time loop
                        
                    Array.Copy(phiHost, prevPhi, phiHost.Length); // Store for next check
                }
            }

            // Synchronize to ensure all GPU work is complete
            accelerator.Synchronize();
            
            // Copy final distribution functions back to CPU
            var finalFi = fiBuffer.GetAsArray1D();

            // Extract solution and update original mesh elements
            var dict = new Dictionary<int, double>(elementCount);
            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                int baseIndex = idx * 9; // Base index for this element's 9 distributions

                // Copy distribution functions back to element
                for (int k = 0; k < 9; k++)
                {
                    element.Fi[k] = finalFi[baseIndex + k];
                    element.Fi_next[k] = 0.0; // Clear temporary storage
                }

                // Compute potential as sum of distributions
                double phi = 0.0;
                for (int k = 0; k < 9; k++)
                    phi += finalFi[baseIndex + k];
                    
                dict[elementIds[idx]] = phi; // Store in solution dictionary
            }

            // Create solution object and update mesh
            var pd = new PotentialDistribution(dict);
            lbmGrid.SetPotentialDistribution(pd);
            return pd;
        }

        /// <summary>
        /// Ensures CUDA kernels are compiled and cached for execution.
        /// Uses thread-safe lazy initialization to avoid recompilation overhead.
        /// </summary>
        private static void EnsureCudaKernels()
        {
            // Initialize CUDA context first
            LatticeBoltzmannCudaContext.EnsureInitialized();
            
            // Quick check without locking for performance
            if (_initializeKernel != null)
                return;

            // Thread-safe kernel compilation using double-checked locking
            lock (_cudaKernelLock)
            {
                // Double-check after acquiring lock
                if (_initializeKernel != null)
                    return;

                // Get accelerator for kernel compilation
                var accelerator = LatticeBoltzmannCudaContext.Accelerator;
                
                // Compile and cache all LBM kernels with automatic optimization
                // ILGPU handles thread block sizing and register allocation automatically
                _initializeKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(InitializeKernel);
                _collisionKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<double>, double, double, ArrayView<double>>(CollisionKernel);
                _streamKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>>(StreamingKernel);
                _updateKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(UpdateKernel);
                _phiKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>>(PhiKernel);
            }
        }

        /// <summary>
        /// CUDA kernel for initializing distribution functions and boundary conditions.
        /// Executed in parallel across all mesh elements on GPU.
        /// Each thread handles one element's initialization.
        /// </summary>
        /// <param name="index">Linear element index (thread ID)</param>
        /// <param name="fi">Output: distribution functions [elementCount * 9]</param>
        /// <param name="fiNext">Output: temporary array (zeroed) [elementCount * 9]</param>
        /// <param name="isWall">Input: wall flags [elementCount]</param>
        /// <param name="isElectrode">Input: electrode flags [elementCount]</param>
        /// <param name="electrodeIsSource">Input: boundary type flags [elementCount]</param>
        /// <param name="electrodeCurrent">Input: current values [elementCount]</param>
        /// <param name="electrodePotential">Input: potential values [elementCount]</param>
        /// <param name="neighborIsWall">Input: neighbor wall flags [elementCount * 9]</param>
        /// <param name="opposite">Input: opposite direction mapping [9]</param>
        /// <param name="weights">Input: D2Q9 equilibrium weights [9]</param>
        private static void InitializeKernel(
            Index1D index,                          // Current thread's element index
            ArrayView<double> fi,                   // Distribution functions to initialize
            ArrayView<double> fiNext,               // Temporary array (cleared)
            ArrayView<int> isWall,                  // Wall identification per element
            ArrayView<int> isElectrode,             // Electrode identification per element
            ArrayView<int> electrodeIsSource,       // Boundary condition type per element
            ArrayView<double> electrodeCurrent,     // Current values for Neumann BCs
            ArrayView<double> electrodePotential,   // Potential values for Dirichlet BCs
            ArrayView<int> neighborIsWall,          // Wall flags for each neighbor
            ArrayView<int> opposite,                // Opposite direction mapping
            ArrayView<double> weights)              // D2Q9 equilibrium weights
        {
            // Calculate base index for this element's 9 distribution functions
            int baseIndex = index * 9;

            // Check if current element is a wall
            bool wall = isWall[index] == 1;

            // Initialize all 9 distribution functions for current element
            for (int k = 0; k < 9; k++)
            {
                fiNext[baseIndex + k] = 0.0;                   // Clear temporary storage
                fi[baseIndex + k] = wall ? 0.0 : weights[k];   // Walls=0, fluid=equilibrium weights
            }

            // Skip further processing for wall elements
            if (wall)
                return;

            // Apply electrode boundary conditions if this element is an electrode
            if (isElectrode[index] == 1)
            {
                // Check boundary condition type
                if (electrodeIsSource[index] == 1)
                {
                    // Neumann boundary condition: prescribed current
                    double current = electrodeCurrent[index];
                    
                    // Set distribution functions proportional to current
                    for (int k = 0; k < 9; k++)
                    {
                        double value = weights[k] * current;
                        fi[baseIndex + k] = value;
                    }

                    // Apply bounce-back for directions pointing into walls
                    for (int k = 0; k < 9; k++)
                    {
                        // Check if neighbor in direction k is a wall
                        if (neighborIsWall[baseIndex + k] == 1)
                        {
                            int opp = opposite[k];              // Get opposite direction
                            double value = fi[baseIndex + k];   // Get distribution value
                            fi[baseIndex + opp] += value;       // Add to opposite direction
                            fi[baseIndex + k] = 0.0;           // Remove from original direction
                        }
                    }
                }
                else
                {
                    // Dirichlet boundary condition: prescribed potential
                    double potential = electrodePotential[index];
                    
                    // Set distribution functions proportional to potential
                    for (int k = 0; k < 9; k++)
                        fi[baseIndex + k] = weights[k] * potential;
                }
            }
        }

        /// <summary>
        /// CUDA kernel for BGK collision step of LBM algorithm.
        /// Relaxes distribution functions toward local equilibrium based on material properties.
        /// Each thread processes one fluid element.
        /// </summary>
        /// <param name="index">Linear element index (thread ID)</param>
        /// <param name="fi">Input/Output: distribution functions [elementCount * 9]</param>
        /// <param name="isWall">Input: wall flags [elementCount]</param>
        /// <param name="conductivity">Input: material conductivity [elementCount]</param>
        /// <param name="csSquared">Input: lattice speed of sound squared</param>
        /// <param name="minTau">Input: minimum relaxation time for stability</param>
        /// <param name="weights">Input: D2Q9 equilibrium weights [9]</param>
        private static void CollisionKernel(
            Index1D index,                      // Current thread's element index
            ArrayView<double> fi,               // Distribution functions to update
            ArrayView<int> isWall,              // Wall identification per element
            ArrayView<double> conductivity,     // Material conductivity per element
            double csSquared,                   // Lattice speed of sound squared
            double minTau,                      // Minimum relaxation time for stability
            ArrayView<double> weights)          // D2Q9 equilibrium weights
        {
            // Skip wall elements (no collision)
            if (isWall[index] == 1)
                return;

            // Calculate base index for this element's 9 distributions
            int baseIndex = index * 9;
            
            // Compute macroscopic potential (zeroth moment)
            double phi = 0.0;
            for (int k = 0; k < 9; k++)
                phi += fi[baseIndex + k];

            // Calculate relaxation time from material conductivity
            // τ = D/cs² + 0.5, where D is the diffusion coefficient (conductivity)
            double tau = conductivity[index] / csSquared + 0.5;
            
            // Enforce minimum relaxation time for numerical stability
            if (tau < minTau)
                tau = minTau;
                
            double omega = 1.0 / tau; // Collision frequency (inverse relaxation time)

            // Apply BGK collision operator: Fi = Fi - ω(Fi - Fi_eq)
            for (int k = 0; k < 9; k++)
            {
                double geq = weights[k] * phi;          // Local equilibrium distribution
                double value = fi[baseIndex + k];       // Current distribution
                fi[baseIndex + k] = value - omega * (value - geq); // Relax toward equilibrium
            }
        }

        /// <summary>
        /// CUDA kernel for streaming step of LBM algorithm.
        /// Propagates distribution functions to neighboring elements according to velocity directions.
        /// Uses atomic operations to handle race conditions in parallel execution.
        /// </summary>
        /// <param name="index">Linear element index (thread ID)</param>
        /// <param name="fi">Input: source distribution functions [elementCount * 9]</param>
        /// <param name="fiNext">Output: destination distribution functions [elementCount * 9]</param>
        /// <param name="isWall">Input: wall flags [elementCount]</param>
        /// <param name="neighborIndices">Input: neighbor connectivity [elementCount * 9]</param>
        /// <param name="neighborIsWall">Input: neighbor wall flags [elementCount * 9]</param>
        /// <param name="opposite">Input: opposite direction mapping [9]</param>
        private static void StreamingKernel(
            Index1D index,                      // Current thread's element index
            ArrayView<double> fi,               // Source distribution functions
            ArrayView<double> fiNext,           // Destination distribution functions
            ArrayView<int> isWall,              // Wall identification per element
            ArrayView<int> neighborIndices,     // Neighbor connectivity array
            ArrayView<int> neighborIsWall,      // Neighbor wall flags
            ArrayView<int> opposite)            // Opposite direction mapping
        {
            // Skip wall elements (no streaming)
            if (isWall[index] == 1)
                return;

            // Calculate base index for this element's 9 distributions
            int baseIndex = index * 9;
            
            // Stream all 9 distribution functions to their target locations
            for (int k = 0; k < 9; k++)
            {
                double value = fi[baseIndex + k];              // Distribution to stream
                int neighborIndex = neighborIndices[baseIndex + k]; // Target neighbor index

                // Check if neighbor exists and is not a wall
                if (neighborIndex >= 0 && neighborIsWall[baseIndex + k] == 0)
                {
                    // Normal streaming: propagate to same direction in neighbor
                    ref double destination = ref fiNext[neighborIndex * 9 + k];
                    Atomic.Exchange(ref destination, value); // Atomic operation prevents race conditions
                }
                // Check if neighbor exists but is a wall (bounce-back required)
                else if (neighborIndex >= 0)
                {
                    // Bounce-back: reflect distribution to opposite direction in current element
                    int opp = opposite[k];
                    ref double bounceDestination = ref fiNext[baseIndex + opp];
                    Atomic.Exchange(ref bounceDestination, value); // Atomic add for multiple bounces
                }
                // If neighborIndex < 0, distribution leaves domain (absorbed at boundary)
            }
        }

        /// <summary>
        /// CUDA kernel for updating distribution functions and enforcing boundary conditions.
        /// Copies streamed values and re-applies electrode boundary conditions.
        /// Each thread processes one element.
        /// </summary>
        /// <param name="index">Linear element index (thread ID)</param>
        /// <param name="fi">Output: updated distribution functions [elementCount * 9]</param>
        /// <param name="fiNext">Input: streamed distribution functions [elementCount * 9]</param>
        /// <param name="isWall">Input: wall flags [elementCount]</param>
        /// <param name="isElectrode">Input: electrode flags [elementCount]</param>
        /// <param name="electrodeIsSource">Input: boundary type flags [elementCount]</param>
        /// <param name="electrodeCurrent">Input: current values [elementCount]</param>
        /// <param name="electrodePotential">Input: potential values [elementCount]</param>
        /// <param name="neighborIsWall">Input: neighbor wall flags [elementCount * 9]</param>
        /// <param name="opposite">Input: opposite direction mapping [9]</param>
        /// <param name="weights">Input: D2Q9 equilibrium weights [9]</param>
        private static void UpdateKernel(
            Index1D index,                      // Current thread's element index
            ArrayView<double> fi,               // Distribution functions to update
            ArrayView<double> fiNext,           // Streamed distribution functions
            ArrayView<int> isWall,              // Wall identification per element
            ArrayView<int> isElectrode,         // Electrode identification per element
            ArrayView<int> electrodeIsSource,   // Boundary condition type per element
            ArrayView<double> electrodeCurrent, // Current values for Neumann BCs
            ArrayView<double> electrodePotential, // Potential values for Dirichlet BCs
            ArrayView<int> neighborIsWall,      // Neighbor wall flags
            ArrayView<int> opposite,            // Opposite direction mapping
            ArrayView<double> weights)          // D2Q9 equilibrium weights
        {
            // Skip wall elements (no update needed)
            if (isWall[index] == 1)
                return;

            // Calculate base index for this element's 9 distributions
            int baseIndex = index * 9;
            
            // Copy streamed values from temporary to main arrays
            for (int k = 0; k < 9; k++)
            {
                fi[baseIndex + k] = fiNext[baseIndex + k];  // Copy streamed value
                fiNext[baseIndex + k] = 0.0;                // Clear temporary for next iteration
            }

            // Skip boundary condition enforcement for non-electrodes
            if (isElectrode[index] == 0)
                return;

            // Re-enforce electrode boundary conditions after streaming
            if (electrodeIsSource[index] == 1)
            {
                // Neumann boundary condition: prescribed current
                double current = electrodeCurrent[index];
                
                // Reset distribution functions to current value
                for (int k = 0; k < 9; k++)
                {
                    double value = weights[k] * current;
                    fi[baseIndex + k] = value;
                }

                // Apply bounce-back for directions pointing into walls
                for (int k = 0; k < 9; k++)
                {
                    if (neighborIsWall[baseIndex + k] == 1)
                    {
                        int opp = opposite[k];              // Get opposite direction
                        double value = fi[baseIndex + k];   // Get distribution value
                        fi[baseIndex + opp] += value;       // Add to opposite direction
                        fi[baseIndex + k] = 0.0;           // Remove from original direction
                    }
                }
            }
            else
            {
                // Dirichlet boundary condition: prescribed potential
                double potential = electrodePotential[index];
                
                // Reset distribution functions to potential value
                for (int k = 0; k < 9; k++)
                    fi[baseIndex + k] = weights[k] * potential;
            }
        }

        /// <summary>
        /// CUDA kernel for computing macroscopic potential field from distribution functions.
        /// Used for convergence checking and final solution extraction.
        /// Each thread processes one element and computes φ = Σ Fi.
        /// </summary>
        /// <param name="index">Linear element index (thread ID)</param>
        /// <param name="fi">Input: distribution functions [elementCount * 9]</param>
        /// <param name="phiOut">Output: potential field [elementCount]</param>
        private static void PhiKernel(
            Index1D index,                      // Current thread's element index
            ArrayView<double> fi,               // Distribution functions
            ArrayView<double> phiOut)           // Output potential field
        {
            // Calculate base index for this element's 9 distributions
            int baseIndex = index * 9;
            
            // Sum all 9 distribution functions to get macroscopic potential
            double phi = 0.0;
            for (int k = 0; k < 9; k++)
                phi += fi[baseIndex + k];
                
            // Store result in output array
            phiOut[index] = phi;
        }
    }
}
