using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Solvers;

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
        private const double TauSafetyEpsilon = 0.01;          // Safety margin added to τ>0.5 stability limit (gives τ≥0.51)
        private const double MinTau = 0.5 + TauSafetyEpsilon; // Minimum relaxation time keeps BGK update from becoming singular
        private const double ElectrodeFluxRelaxation = 0.25;   // Small factor that damps electrode flux corrections for stability
        private const int PhiMonitoringInterval = 50;         // How often to print the global potential sum for debugging

        // CUDA kernel management - static to share across solver instances
        private static readonly object _cudaKernelLock = new(); // Thread-safe kernel compilation

        // Pre-compiled CUDA kernels for LBM operations
        private static System.Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _initializeKernel;
        private static System.Action<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<double>, double, double, ArrayView<double>>? _collisionKernel;
        private static System.Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>>? _streamKernel;
        private static System.Action<Index1D, UpdateKernelParams>? _updateKernel;
        private static System.Action<Index1D, ArrayView<double>, ArrayView<double>>? _phiKernel;
        // A blittable container for Update kernel arguments to reduce generic arity
        private readonly struct UpdateKernelParams
        {
            public readonly ArrayView<double> Fi;
            public readonly ArrayView<double> FiNext;
            public readonly ArrayView<int> IsWall;
            public readonly ArrayView<int> IsElectrode;
            public readonly ArrayView<int> ElectrodeIsSource;
            public readonly ArrayView<int> ElectrodeIsGround;
            public readonly ArrayView<double> ElectrodeCurrent;
            public readonly ArrayView<double> ElectrodePotential;
            public readonly ArrayView<int> NeighborIsWall;
            public readonly ArrayView<int> NeighborIndices;
            public readonly ArrayView<double> Conductivity;
            public readonly ArrayView<double> PhiStreamed;
            public readonly ArrayView<int> Opposite;
            public readonly ArrayView<double> Weights;
            public readonly double ElectrodeFluxRelaxationFactor;
            public readonly int AnchorElementIndex;
            public readonly double AnchorPotential;

            public UpdateKernelParams(
                ArrayView<double> fi,
                ArrayView<double> fiNext,
                ArrayView<int> isWall,
                ArrayView<int> isElectrode,
                ArrayView<int> electrodeIsSource,
                ArrayView<int> electrodeIsGround,
                ArrayView<double> electrodeCurrent,
                ArrayView<double> electrodePotential,
                ArrayView<int> neighborIsWall,
                ArrayView<int> neighborIndices,
                ArrayView<double> conductivity,
                ArrayView<double> phiStreamed,
                ArrayView<int> opposite,
                ArrayView<double> weights,
                double electrodeFluxRelaxationFactor,
                int anchorElementIndex,
                double anchorPotential)
            {
                Fi = fi;
                FiNext = fiNext;
                IsWall = isWall;
                IsElectrode = isElectrode;
                ElectrodeIsSource = electrodeIsSource;
                ElectrodeIsGround = electrodeIsGround;
                ElectrodeCurrent = electrodeCurrent;
                ElectrodePotential = electrodePotential;
                NeighborIsWall = neighborIsWall;
                NeighborIndices = neighborIndices;
                Conductivity = conductivity;
                PhiStreamed = phiStreamed;
                Opposite = opposite;
                Weights = weights;
                ElectrodeFluxRelaxationFactor = electrodeFluxRelaxationFactor;
                AnchorElementIndex = anchorElementIndex;
                AnchorPotential = anchorPotential;
            }
        }

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
            if (double.IsNaN(conductivity) || double.IsInfinity(conductivity) || !double.IsFinite(conductivity))
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
            int bcElectrodeCount = bcElectrodes.Count;

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
        /// CPU reference implementation of the D2Q9 diffusion LBM.
        /// The loop order is intentionally identical to the CUDA path so that
        /// both code paths evolve the same discrete dynamics and can be debugged
        /// against one another line by line.
        /// </summary>
        private PotentialDistribution RunForward(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            // Cache solver parameters locally to avoid repeated property lookups.
            int maxIter = MaxIterationCount;
            double tol = SolutionTolerance;
            int checkFreq = ConvergenceCheckFrequency;

            // LBM constants required by every step of the algorithm.
            var weights = LatticeBoltzmannConstants.Weights;       // D2Q9 equilibrium weights
            var opposite = LatticeBoltzmannConstants.Opposite;     // Mapping from a direction to its opposite
            double csSquared = LatticeBoltzmannConstants.CsSquared; // c_s^2 used in the diffusion relation τ = D/c_s^2 + 0.5

            // Pull a dense list of elements and electrodes once so that the solver
            // can operate on cache-friendly arrays instead of repeatedly querying
            // the grid object.
            var elements = lbmGrid.GetElements().Cast<LBMElement>().ToList();
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().Cast<LBMElectrode>().ToList();

            // Map each lattice id to its position inside the dense element list.
            // This allows us to jump between topological ids and array indices when
            // we need neighbor data for boundary corrections.
            var elementIndexLookup = elements
                .Select((element, idx) => new { element.Id, Index = idx })
                .ToDictionary(entry => entry.Id, entry => entry.Index);

            // Build a lookup of electrode descriptors keyed by the lattice cell id.
            // Boundary-condition electrodes override the mesh ones so that imposed
            // currents/potentials coming from the caller always win.
            var electrodeByGridId = new Dictionary<int, LBMElectrode>();
            foreach (var e in electrodes)
                electrodeByGridId[e.GridId] = e;
            foreach (var e in bcElectrodes)
                electrodeByGridId[e.GridId] = e;

            // We use the stored potential distribution as the initial macroscopic
            // state so that repeated solves continue from the previous solution
            // rather than from an arbitrary zero field.
            var initialPotential = lbmGrid.GetPotentialDistribution();

            // Conductivity distribution is required both for the BGK relaxation time
            // and for the electrode corrections that approximate Neumann fluxes.
            var sigmaDist = lbmGrid.GetConductivityDistribution();

            foreach (var element in elements)
            {
                // Walls are perfectly insulating in this model, so clamp the
                // conductivity to zero and make sure the global conductivity
                // distribution mirrors the sanitized value.
                if (element.IsWall)
                {
                    element.Conductivity = 0.0;
                    sigmaDist.Conductivities[element.Id] = 0.0;
                }
                else
                {
                    double sigma = SanitizeConductivity(sigmaDist.GetConductivity(element.Id));
                    element.Conductivity = sigma;
                    sigmaDist.Conductivities[element.Id] = sigma;
                }

                // Mark electrode cells once so that the time loop does not need to
                // check dictionaries repeatedly.
                element.IsElectrode = electrodeByGridId.ContainsKey(element.Id);

                // Determine the macroscopic potential used to seed the distribution
                // functions.  We start from the last known potential (or zero if
                // none is stored) and prefer any explicitly prescribed electrode
                // potential so that Dirichlet data is satisfied from the first step.
                double phi0 = initialPotential?.GetValue(element.Id) ?? 0.0;
                if (element.IsElectrode && electrodeByGridId.TryGetValue(element.Id, out var electrodeDescriptor))
                {
                    if (!double.IsNaN(electrodeDescriptor.Potential))
                        phi0 = electrodeDescriptor.Potential;
                }

                // Initialize the distributions in exact equilibrium: Fi = w_i * φ.
                // Starting from equilibrium eliminates the artificial transients that
                // would otherwise arise if we injected an anisotropic population.
                for (int k = 0; k < 9; k++)
                {
                    element.Fi[k] = element.IsWall ? 0.0 : weights[k] * phi0;
                    element.Fi_next[k] = 0.0; // streaming buffer is cleared once up-front
                }
            }

            // Choose a single electrode that will act as the Dirichlet anchor used
            // to remove the null-space (constant) mode.  Preference is given to the
            // ground electrode supplied by the boundary condition.
            LBMElectrode? anchorElectrode = bcElectrodes.FirstOrDefault(e => e.IsGround)
                ?? electrodes.FirstOrDefault(e => e.IsGround);
            int anchorGridId = anchorElectrode?.GridId ?? -1;
            double anchorPotential = anchorElectrode?.Potential ?? 0.0;

            int elementCount = elements.Count;
            double[] prevPhi = new double[elementCount];           // potentials from the last convergence check
            double[] phiStreamed = new double[elementCount];        // potentials reconstructed immediately after streaming
            double[] phiAfterBoundary = new double[elementCount];   // potentials after boundary conditions are enforced

            for (int t = 0; t < maxIter; t++)
            {
                // --- Collision ----------------------------------------------------
                foreach (var element in elements)
                {
                    if (element.IsWall)
                        continue; // walls remain empty; there is nothing to collide

                    // Macroscopic potential is the zeroth moment of the populations.
                    double phi = 0.0;
                    for (int k = 0; k < 9; k++)
                        phi += element.Fi[k];

                    // Diffusion relation τ = γ/c_s^2 + 0.5 links conductivity (γ) and
                    // the BGK relaxation time.  Clamping τ > 0.5 keeps the relaxation
                    // operator positive definite and therefore stable.
                    double tau = ComputeRelaxationTime(element.Conductivity, csSquared);
                    double omega = 1.0 / tau;

                    // Relax every population towards the isotropic equilibrium
                    // corresponding to the current potential.  This implements the
                    // discrete diffusion equation (Chapman–Enskog analysis) and is
                    // the only source of dissipation in the scheme.
                    for (int k = 0; k < 9; k++)
                        element.Fi[k] += omega * (weights[k] * phi - element.Fi[k]);
                }

                // Clear the streaming buffers before propagating the post-collision
                // populations.  Keeping this explicit mirrors the CUDA implementation
                // and guarantees that no stale values survive across iterations.
                foreach (var element in elements)
                {
                    for (int k = 0; k < 9; k++)
                        element.Fi_next[k] = 0.0;
                }

                // --- Streaming ---------------------------------------------------
                foreach (var element in elements)
                {
                    if (element.IsWall)
                        continue; // wall cells neither send nor receive populations

                    for (int k = 0; k < 9; k++)
                    {
                        var neighbor = element.Neighbors[k];
                        double value = element.Fi[k];

                        if (neighbor != null && !neighbor.IsWall)
                        {
                            // Normal streaming: propagate the population into the
                            // same velocity slot of the neighbor cell.
                            neighbor.Fi_next[k] = value;
                        }
                        else if (neighbor != null)
                        {
                            // Bounce-back for solid walls preserves zero normal flux
                            // by reflecting the population back along the opposite
                            // discrete velocity.
                            element.Fi_next[opposite[k]] = value;
                        }
                        // Populations leaving the domain (null neighbor) are simply
                        // discarded, which matches the behavior of the CUDA kernel.
                    }
                }

                // --- Macroscopic reconstruction (post streaming) -----------------
                for (int idx = 0; idx < elementCount; idx++)
                {
                    double phi = 0.0;
                    var element = elements[idx];
                    for (int k = 0; k < 9; k++)
                        phi += element.Fi_next[k];
                    phiStreamed[idx] = phi;
                }

                // --- Electrode boundary conditions -------------------------------
                ApplyElectrodeBoundaryConditions(
                    elements,
                    elementIndexLookup,
                    electrodeByGridId,
                    phiStreamed,
                    weights,
                    opposite,
                    anchorGridId,
                    anchorPotential);

                // Reconstruct the potential field after the boundary corrections so
                // that convergence monitoring and diagnostics see the final state of
                // the iteration.
                for (int idx = 0; idx < elementCount; idx++)
                {
                    double phi = 0.0;
                    var element = elements[idx];
                    for (int k = 0; k < 9; k++)
                        phi += element.Fi_next[k];
                    phiAfterBoundary[idx] = phi;
                }

                // Periodically print the global potential sum as a sanity check.
                if (PhiMonitoringInterval > 0 && t % PhiMonitoringInterval == 0)
                {
                    double globalPhi = phiAfterBoundary.Sum();
                    System.Diagnostics.Debug.WriteLine($"[LBM-CPU] Iter {t}: Σφ = {globalPhi}");
                }

                // Copy the streamed-and-corrected populations back into Fi so that
                // the next iteration starts from the freshly updated state.
                foreach (var element in elements)
                {
                    if (element.IsWall)
                    {
                        Array.Clear(element.Fi, 0, element.Fi.Length);
                        continue;
                    }

                    for (int k = 0; k < 9; k++)
                        element.Fi[k] = element.Fi_next[k];
                }

                // --- Convergence check ------------------------------------------
                if (t % checkFreq == 0)
                {
                    double num = 0.0;
                    double den = 0.0;
                    for (int i = 0; i < elementCount; i++)
                    {
                        double diff = phiAfterBoundary[i] - prevPhi[i];
                        num += diff * diff;
                        den += phiAfterBoundary[i] * phiAfterBoundary[i];
                    }

                    if (den > 0.0 && Math.Sqrt(num / den) < tol)
                        break;

                    Array.Copy(phiAfterBoundary, prevPhi, elementCount);
                }
            }

            // Create the final potential distribution (φ = Σ_i Fi) for the caller.
            var result = new Dictionary<int, double>(elementCount);
            foreach (var element in elements)
            {
                double phi = 0.0;
                for (int k = 0; k < 9; k++)
                    phi += element.Fi[k];
                result[element.Id] = phi;
            }

            var potentialDistribution = new PotentialDistribution(result);
            lbmGrid.SetPotentialDistribution(potentialDistribution);
            return potentialDistribution;
        }

        /// <summary>
        /// Determines the discrete velocity direction that points from the electrode
        /// element towards the exterior wall.  This is used to decide which populations
        /// represent the outward normal and the inward facing counterpart when enforcing
        /// Neumann current constraints.
        /// </summary>
        private static int FindOutwardNormalDirection(LBMElement element, IReadOnlyList<int> opposite)
        {
            int outwardDirection = -1;

            for (int dir = 1; dir < 9; dir++)
            {
                var neighbor = element.Neighbors[dir];
                bool neighborIsWall = neighbor == null || neighbor.IsWall;
                if (!neighborIsWall)
                    continue;

                int inward = opposite[dir];
                var interiorNeighbor = element.Neighbors[inward];
                if (interiorNeighbor == null || interiorNeighbor.IsWall)
                    continue;

                if (outwardDirection < 0 || dir < 5 && outwardDirection >= 5)
                    outwardDirection = dir;
            }

            return outwardDirection;
        }

        /// <summary>
        /// Applies the electrode boundary conditions to the streamed populations.
        /// The corrections are performed on Fi_next so that both the CPU and CUDA
        /// implementations operate on the same data layout.
        /// </summary>
        private static void ApplyElectrodeBoundaryConditions(
            IReadOnlyList<LBMElement> elements,
            IReadOnlyDictionary<int, int> elementIndexLookup,
            IReadOnlyDictionary<int, LBMElectrode> electrodeByGridId,
            IReadOnlyList<double> phiStreamed,
            IReadOnlyList<double> weights,
            IReadOnlyList<int> opposite,
            int anchorGridId,
            double anchorPotential)
        {
            foreach (var element in elements)
            {
                if (!element.IsElectrode)
                    continue; // Skip non-electrode cells entirely

                if (!elementIndexLookup.TryGetValue(element.Id, out int elementIndex))
                    continue; // Should never happen, but guard against inconsistent meshes

                if (!electrodeByGridId.TryGetValue(element.Id, out var electrode))
                    continue; // Electrodes may have been removed from the BC definition

                if (element.Id == anchorGridId)
                {
                    // Dirichlet anchor: enforce a fixed potential by rebuilding the
                    // equilibrium populations directly.  This removes the gauge freedom
                    // of the electric potential and prevents global drift.
                    for (int k = 0; k < 9; k++)
                        element.Fi_next[k] = weights[k] * anchorPotential;
                    continue;
                }

                int outwardDirection = FindOutwardNormalDirection(element, opposite);
                if (outwardDirection < 0)
                    continue; // Could not determine a well-defined outward normal

                int inwardDirection = opposite[outwardDirection];
                var interiorNeighbor = element.Neighbors[inwardDirection];
                if (interiorNeighbor == null || interiorNeighbor.IsWall)
                    continue; // No interior cell to exchange current with

                if (!elementIndexLookup.TryGetValue(interiorNeighbor.Id, out int interiorIndex))
                    continue;

                // Discrete potential gradient across the electrode interface.
                double phiBoundary = phiStreamed[elementIndex];
                double phiInterior = phiStreamed[interiorIndex];
                double sigmaBoundary = element.Conductivity;
                double sigmaInterior = interiorNeighbor.Conductivity;

                // Average conductivity yields a symmetric flux approximation similar to
                // harmonic averaging in finite-volume schemes.  This remains well behaved
                // even when one of the cells is much more conductive than the other.
                double sigmaAvg = 0.5 * (sigmaBoundary + sigmaInterior);
                double normalFlux = sigmaAvg * (phiInterior - phiBoundary);
                double targetFlux = electrode.Current; // Prescribed normal current density (Neumann data)
                double deltaFlux = normalFlux - targetFlux;

                // Convert the flux error into a population correction.  Multiplying by the
                // directional weight keeps the correction consistent with the equilibrium
                // definition and the factor ElectrodeFluxRelaxation damps the update to
                // avoid over-correction on coarse meshes.
                double deltaFi = ElectrodeFluxRelaxation * deltaFlux * weights[inwardDirection];

                if (electrode.IsExcitation)
                {
                    // Source electrode injects current into the domain.  Increasing the
                    // inward population and reducing the outward one preserves mass while
                    // biasing the flux in the desired direction.
                    element.Fi_next[inwardDirection] += deltaFi;
                    element.Fi_next[outwardDirection] -= deltaFi;
                }
                else
                {
                    // Sink/measurement electrode removes current from the domain.  The
                    // opposite adjustment enforces the sign convention without changing
                    // the total sum of the distributions.
                    element.Fi_next[inwardDirection] -= deltaFi;
                    element.Fi_next[outwardDirection] += deltaFi;
                }
            }
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
            var elementIndexLookup = elementIds
                .Select((id, idx) => new { id, idx })
                .ToDictionary(v => v.id, v => v.idx);   // Map from lattice id to linear index

            // Get electrode data for boundary condition setup
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToArray();
            var bcElectrodes = bc.GetElectrodes().Cast<LBMElectrode>().ToArray();

            // Create fast lookup dictionaries for electrode properties
            var electrodeByGridId = electrodes.ToDictionary(e => e.GridId);
            var bcElectrodeById = bcElectrodes.ToDictionary(e => e.Id);
            var bcElectrodeByGridId = bcElectrodes.ToDictionary(e => e.GridId);
            var electrodeLookup = new Dictionary<int, LBMElectrode>();
            foreach (var e in electrodes)
                electrodeLookup[e.GridId] = e;
            foreach (var e in bcElectrodes)
                electrodeLookup[e.GridId] = e;

            // Record the initial macroscopic potential (if any) and the Dirichlet anchor.
            var initialPotential = lbmGrid.GetPotentialDistribution();
            var anchorElectrode = bcElectrodes.FirstOrDefault(e => e.IsGround)
                ?? electrodes.FirstOrDefault(e => e.IsGround);
            int anchorGridId = anchorElectrode?.GridId ?? -1;
            double anchorPotential = anchorElectrode?.Potential ?? 0.0;
            int anchorElementIndex = anchorGridId >= 0 && elementIndexLookup.TryGetValue(anchorGridId, out int idxAnchor)
                ? idxAnchor
                : -1;

            // Extract pre-computed topology arrays
            var isWallHost = topology.IsWall;               // Wall flags for each element
            var neighborIndicesHost = topology.NeighborIndices;  // Flattened neighbor connectivity
            var neighborIsWallHost = topology.NeighborIsWall;    // Neighbor wall flags

            // Prepare host arrays for GPU transfer
            var isElectrodeHost = new int[elementCount];        // Electrode identification flags
            var electrodeIsSourceHost = new int[elementCount];   // Source (Neumann) vs sink (Dirichlet) flags
            var electrodeIsGroundHost = new int[elementCount];   // Ground electrode flags for Neumann BCs
            var electrodeCurrentHost = new double[elementCount]; // Current values for source electrodes
            var electrodePotentialHost = new double[elementCount]; // Potential values for sink electrodes
            var conductivityHost = new double[elementCount];     // Material conductivity per element
            var initialPhiHost = new double[elementCount];       // Initial macroscopic potential per element

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

                // Default initialization assumes a fluid cell with no boundary data.
                double phi0 = initialPotential?.GetValue(element.Id) ?? 0.0;
                bool isElectrode = false;
                int isSource = 0;
                double electrodeCurrent = 0.0;
                double electrodePotential = phi0;

                if (electrodeLookup.TryGetValue(element.Id, out var electrode))
                {
                    isElectrode = true;
                    electrodeCurrent = electrode.Current;
                    electrodePotential = electrode.Potential;
                    if (!double.IsNaN(electrode.Potential))
                        phi0 = electrode.Potential;
                    if (electrode.IsExcitation)
                        isSource = 1;
                }

                element.IsElectrode = isElectrode;
                isElectrodeHost[idx] = isElectrode ? 1 : 0;
                electrodeIsSourceHost[idx] = isSource;
                electrodeIsGroundHost[idx] = element.Id == anchorGridId ? 1 : 0;
                electrodeCurrentHost[idx] = electrodeCurrent;
                electrodePotentialHost[idx] = electrodePotential;
                initialPhiHost[idx] = phi0;
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
            using var initialPhiBuffer = accelerator.Allocate1D<double>(elementCount);   // Initial macroscopic potential
            using var conductivityBuffer = accelerator.Allocate1D<double>(elementCount);  // Material properties
            using var isWallBuffer = accelerator.Allocate1D<int>(elementCount);          // Wall identification
            using var isElectrodeBuffer = accelerator.Allocate1D<int>(elementCount);     // Electrode identification
            using var electrodeIsSourceBuffer = accelerator.Allocate1D<int>(elementCount); // Boundary type flags
            using var electrodeIsGroundBuffer = accelerator.Allocate1D<int>(elementCount); // Ground electrode flags
            using var electrodeCurrentBuffer = accelerator.Allocate1D<double>(elementCount); // Current values
            using var electrodePotentialBuffer = accelerator.Allocate1D<double>(elementCount); // Potential values
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(elementCount * 9);     // Neighbor connectivity
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(elementCount * 9);   // Neighbor wall flags
            using var phiBuffer = accelerator.Allocate1D<double>(elementCount);          // Potential field for convergence

            // Copy host data to GPU memory
            isWallBuffer.CopyFromCPU(isWallHost);
            isElectrodeBuffer.CopyFromCPU(isElectrodeHost);
            electrodeIsSourceBuffer.CopyFromCPU(electrodeIsSourceHost);
            electrodeIsGroundBuffer.CopyFromCPU(electrodeIsGroundHost);
            electrodeCurrentBuffer.CopyFromCPU(electrodeCurrentHost);
            electrodePotentialBuffer.CopyFromCPU(electrodePotentialHost);
            conductivityBuffer.CopyFromCPU(conductivityHost);
            initialPhiBuffer.CopyFromCPU(initialPhiHost);
            neighborIndexBuffer.CopyFromCPU(neighborIndicesHost);
            neighborIsWallBuffer.CopyFromCPU(neighborIsWallHost);

            // Initialize distribution functions and boundary conditions on GPU
            if (_initializeKernel == null)
                throw new NullReferenceException();

            _initializeKernel(elementCount,              // Number of elements to process
                fiBuffer.View,                           // Distribution functions output
                fiNextBuffer.View,                       // Temporary array (zeroed)
                initialPhiBuffer.View,                   // Initial macroscopic potentials
                isWallBuffer.View,                       // Wall identification
                isElectrodeBuffer.View,                  // Electrode identification
                electrodeIsSourceBuffer.View,            // Boundary condition types
                electrodeIsGroundBuffer.View,            // Ground flags for Neumann BCs
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

                // Compute streamed potentials for boundary adjustments
                _phiKernel(elementCount, fiNextBuffer.View, phiBuffer.View);

                // Execute update kernel: copy streamed values and enforce boundary conditions
                var updateParams = new UpdateKernelParams(
                    fiBuffer.View,
                    fiNextBuffer.View,
                    isWallBuffer.View,
                    isElectrodeBuffer.View,
                    electrodeIsSourceBuffer.View,
                    electrodeIsGroundBuffer.View,
                    electrodeCurrentBuffer.View,
                    electrodePotentialBuffer.View,
                    neighborIsWallBuffer.View,
                    neighborIndexBuffer.View,
                    conductivityBuffer.View,
                    phiBuffer.View,
                    LatticeBoltzmannCudaContext.OppositeView,
                    LatticeBoltzmannCudaContext.WeightsView,
                    ElectrodeFluxRelaxation,
                    anchorElementIndex,
                    anchorPotential);

                _updateKernel(elementCount, updateParams);

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
                _initializeKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(InitializeKernel);
                _collisionKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<double>, double, double, ArrayView<double>>(CollisionKernel);
                _streamKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>>(StreamingKernel);
                _updateKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, UpdateKernelParams>(UpdateKernel);
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
        /// <param name="electrodeIsGround">Input: ground electrode flags [elementCount]</param>
        /// <param name="electrodeCurrent">Input: current values [elementCount]</param>
        /// <param name="electrodePotential">Input: potential values [elementCount]</param>
        /// <param name="neighborIsWall">Input: neighbor wall flags [elementCount * 9]</param>
        /// <param name="opposite">Input: opposite direction mapping [9]</param>
        /// <param name="weights">Input: D2Q9 equilibrium weights [9]</param>
        private static void InitializeKernel(
            Index1D index,                          // Current thread's element index
            ArrayView<double> fi,                   // Distribution functions to initialize
            ArrayView<double> fiNext,               // Temporary array (cleared)
            ArrayView<double> initialPhi,           // Initial macroscopic potential for each element
            ArrayView<int> isWall,                  // Wall identification per element
            ArrayView<int> isElectrode,             // Electrode identification per element
            ArrayView<int> electrodeIsSource,       // Boundary condition type per element
            ArrayView<int> electrodeIsGround,       // Ground electrode identification per element
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
            double phi0 = initialPhi[index];

            // Initialize all 9 distribution functions for current element
            for (int k = 0; k < 9; k++)
            {
                fiNext[baseIndex + k] = 0.0;                   // Clear temporary storage
                fi[baseIndex + k] = wall ? 0.0 : weights[k] * phi0; // Start from equilibrium with prescribed potential
            }

            // Skip further processing for wall elements
            if (wall)
                return;
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
        /// <param name="p">Blittable container of all update-kernel parameters</param>
        private static void UpdateKernel(
            Index1D index,                      // Current thread's element index
            UpdateKernelParams p)
        {
            int baseIndex = index * 9;

            if (p.IsWall[index] == 1)
            {
                // Solid walls remain empty so that bounce-back reflections operate on
                // zero populations.  Clearing both Fi and FiNext keeps the GPU path
                // numerically identical to the CPU reference implementation.
                for (int k = 0; k < 9; k++)
                {
                    p.Fi[baseIndex + k] = 0.0;
                    p.FiNext[baseIndex + k] = 0.0;
                }
                return;
            }

            if (p.IsElectrode[index] == 1)
            {
                if (index == p.AnchorElementIndex)
                {
                    // Dirichlet anchor: overwrite the streamed populations with the
                    // isotropic equilibrium corresponding to the fixed potential.
                    for (int k = 0; k < 9; k++)
                        p.FiNext[baseIndex + k] = p.Weights[k] * p.AnchorPotential;
                }
                else
                {
                    int outwardDirection = -1;
                    for (int dir = 1; dir < 9; dir++)
                    {
                        int neighborIndex = p.NeighborIndices[baseIndex + dir];
                        bool neighborIsWall = neighborIndex < 0 || p.NeighborIsWall[baseIndex + dir] == 1;
                        if (!neighborIsWall)
                            continue;

                        int inward = p.Opposite[dir];
                        int interiorIndex = p.NeighborIndices[baseIndex + inward];
                        if (interiorIndex < 0 || p.NeighborIsWall[baseIndex + inward] == 1)
                            continue;

                        if (outwardDirection < 0 || (dir < 5 && outwardDirection >= 5))
                            outwardDirection = dir;
                    }

                    if (outwardDirection >= 0)
                    {
                        int inwardDirection = p.Opposite[outwardDirection];
                        int interiorIndex = p.NeighborIndices[baseIndex + inwardDirection];
                        double phiBoundary = p.PhiStreamed[index];
                        double phiInterior = p.PhiStreamed[interiorIndex];
                        double sigmaBoundary = p.Conductivity[index];
                        double sigmaInterior = p.Conductivity[interiorIndex];
                        double sigmaAvg = 0.5 * (sigmaBoundary + sigmaInterior);
                        double normalFlux = sigmaAvg * (phiInterior - phiBoundary);
                        double targetFlux = p.ElectrodeCurrent[index];
                        double deltaFlux = normalFlux - targetFlux;
                        double deltaFi = p.ElectrodeFluxRelaxationFactor * deltaFlux * p.Weights[inwardDirection];

                        if (p.ElectrodeIsSource[index] == 1)
                        {
                            // Source electrode: increase inward population and decrease
                            // outward population to inject current without changing ΣFi.
                            p.FiNext[baseIndex + inwardDirection] += deltaFi;
                            p.FiNext[baseIndex + outwardDirection] -= deltaFi;
                        }
                        else
                        {
                            // Sink or passive electrode: reverse the correction so that
                            // current is drawn out of the domain while conserving mass.
                            p.FiNext[baseIndex + inwardDirection] -= deltaFi;
                            p.FiNext[baseIndex + outwardDirection] += deltaFi;
                        }
                    }
                }
            }

            // Copy the (possibly corrected) streamed values back into Fi and clear the
            // temporary buffer in preparation for the next streaming step.
            for (int k = 0; k < 9; k++)
            {
                double value = p.FiNext[baseIndex + k];
                p.Fi[baseIndex + k] = value;
                p.FiNext[baseIndex + k] = 0.0;
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
