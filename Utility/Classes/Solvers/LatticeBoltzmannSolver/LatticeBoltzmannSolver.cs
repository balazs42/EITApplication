using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ILGPU;
using ILGPU.Algorithms;
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
        private int MaxIterationCount = 1000;                  // Maximum time steps before forced termination
        private double SolutionTolerance = 1e-8;               // Convergence tolerance for steady-state detection
        private int ConvergenceCheckFrequency = 100;           // How often to check convergence (computational cost)
        private readonly bool _useCuda;                        // Whether to use GPU acceleration

        // LBM stability constants
        private const double TauSafetyEpsilon = 1e-6;          // Small value to prevent numerical instability
        private const double MinTau = 0.5 + TauSafetyEpsilon; // Minimum relaxation time for stability
        // CUDA kernel management - static to share across solver instances
        private static readonly object _cudaKernelLock = new(); // Thread-safe kernel compilation

        // Pre-compiled CUDA kernels for LBM operations
        private static System.Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<double>, ArrayView<double>>? _initializeKernel;
        private static System.Action<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<double>, double, double, ArrayView<double>>? _collisionKernel;
        private static System.Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>>? _streamKernel;
        private static System.Action<Index1D, UpdateKernelParams>? _updateKernel;
        private static System.Action<Index1D, GhostBoundaryKernelParams>? _ghostBoundaryKernel;
        private static System.Action<Index1D, ArrayView<double>, ArrayView<double>>? _phiKernel;

        // A blittable container for Update kernel arguments to reduce generic arity
        private readonly struct UpdateKernelParams
        {
            public readonly ArrayView<double> Fi;
            public readonly ArrayView<double> FiNext;
            public readonly ArrayView<int> IsWall;
            public readonly ArrayView<int> IsGhost;
            public readonly ArrayView<double> PhiStreamed;

            public UpdateKernelParams(
                ArrayView<double> fi,
                ArrayView<double> fiNext,
                ArrayView<int> isWall,
                ArrayView<int> isGhost,
                ArrayView<double> phiStreamed)
            {
                Fi = fi;
                FiNext = fiNext;
                IsWall = isWall;
                IsGhost = isGhost;
                PhiStreamed = phiStreamed;
            }
        }

        private readonly struct GhostBoundaryKernelParams
        {
            public GhostBoundaryKernelParams(
                ArrayView<int> interiorIndices,
                ArrayView<int> ghostIndices,
                ArrayView<int> directions,
                ArrayView<double> fluxPerLink,
                ArrayView<double> fi,
                ArrayView<double> phi,
                ArrayView<double> weights,
                ArrayView<int> opposite,
                ArrayView<double> conductivity,
                double deltaX)
            {
                InteriorIndices = interiorIndices;
                GhostIndices = ghostIndices;
                Directions = directions;
                FluxPerLink = fluxPerLink;
                Fi = fi;
                Phi = phi;
                Weights = weights;
                Opposite = opposite;
                Conductivity = conductivity;
                DeltaX = deltaX;
            }

            public ArrayView<int> InteriorIndices { get; }
            public ArrayView<int> GhostIndices { get; }
            public ArrayView<int> Directions { get; }
            public ArrayView<double> FluxPerLink { get; }
            public ArrayView<double> Fi { get; }
            public ArrayView<double> Phi { get; }
            public ArrayView<double> Weights { get; }
            public ArrayView<int> Opposite { get; }
            public ArrayView<double> Conductivity { get; }
            public double DeltaX { get; }
        }

        /// <summary>
        /// Small container describing how a single lattice cell participates in the
        /// electrode boundary conditions.  The tuple keeps both the geometric index
        /// of the cell and the physical data (current/potential/role) required to
        /// enforce the mixed Dirichlet/Neumann behaviour in a consistent manner on
        /// the CPU implementation.  The same data is mirrored to the GPU buffers in
        /// the CUDA path so that both execution modes follow identical mathematics.
        /// </summary>
        private readonly struct ElectrodeRuntimeData
        {
            public ElectrodeRuntimeData(int electrodeId, int elementIndex, LBMElement element, double current, bool isExcitation, bool isGround)
            {
                ElectrodeId = electrodeId;
                ElementIndex = elementIndex;
                Element = element;
                Current = current;
                IsExcitation = isExcitation;
                IsGround = isGround;
            }

            public int ElectrodeId { get; }
            public int ElementIndex { get; }
            public LBMElement Element { get; }
            public double Current { get; }
            public bool IsExcitation { get; }
            public bool IsGround { get; }
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
        /// Converts a diffusion coefficient (conductivity) to the BGK relaxation time τ.
        /// Relation (a): D = c_s^2 (τ − 1/2) Δt, so τ = D / (c_s^2 Δt) + 1/2.
        /// c_s^2 from <see cref="LatticeBoltzmannConstants.CsSquared"/> only appears in this relation.
        /// </summary>
        /// <param name="diffusionCoefficient">Diffusion coefficient expressed in lattice units (relation (a), Krüger et al.).</param>
        internal static double ComputeTauFromDiffusivityLU(double diffusionCoefficient)
        {
            // Δt_LU = 1 ⇒ τ = D / c_s^2 + 1/2 per relation (a).
            return diffusionCoefficient / LatticeBoltzmannConstants.CsSquared + 0.5;
        }

        /// <summary>
        /// Computes BGK relaxation time from material conductivity and clamps it for stability.
        /// </summary>
        /// <param name="conductivity">Material electrical conductivity (diffusion coefficient).</param>
        /// <returns>Relaxation time ensuring numerical stability.</returns>
        private static double ComputeRelaxationTime(double conductivity)
        {
            double tau = ComputeTauFromDiffusivityLU(conductivity);

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
            PotentialDistribution phi = _useCuda ? RunForwardCuda(lbmGrid, bc) : RunForward(lbmGrid, bc);

            var groundElectrode = bc.GetElectrodes().FirstOrDefault(e => e.Id == boundaryCondition.GroundElectrodeId);
            int groundCellId = groundElectrode?.GridId ?? boundaryCondition.GroundElectrodeId;

            double phiGround = phi.Potentials.TryGetValue(groundCellId, out var storedGround)
                ? storedGround
                : 0.0;
            var shifted = new PotentialDistribution(
                phi.Potentials.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));// - phiGround));
            lbmGrid.SetPotentialDistribution(shifted);

            return shifted;
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
        /// Computes electrode flux densities j_n for each boundary link.  Relation (b) (Krüger et al. for unit
        /// conversion, Gebäck &amp; Heintz for the Neumann BC) distributes currents as flux densities so that
        /// (j_n Δx)/σ is invariant between physical and lattice units.
        /// </summary>
        private static double[] ComputeFluxPerLink(
            IReadOnlyList<LBMBoundaryLink> links,
            IReadOnlyDictionary<int, IReadOnlyList<int>> linksByInterior,
            IReadOnlyList<ElectrodeRuntimeData> electrodes)
        {
            var flux = new double[links.Count];

            if (links.Count == 0 || electrodes.Count == 0)
                return flux;

            var linkOwnership = new Dictionary<int, int>();

            foreach (var group in electrodes.GroupBy(e => e.ElectrodeId))
            {
                double totalCurrent = group.Sum(e => e.Current);
                var perElectrodeLinks = new HashSet<int>();

                foreach (var electrode in group)
                {
                    if (!linksByInterior.TryGetValue(electrode.ElementIndex, out var perCell))
                        continue;

                    foreach (int linkIndex in perCell)
                    {
                        if (!perElectrodeLinks.Add(linkIndex))
                            continue;

                        if (linkOwnership.TryGetValue(linkIndex, out int owner) && owner != group.Key)
                            throw new InvalidOperationException($"Boundary link {linkIndex} referenced by electrodes {owner} and {group.Key}.");

                        linkOwnership[linkIndex] = group.Key;
                    }
                }

                if (perElectrodeLinks.Count == 0)
                    continue;

                double boundaryMeasure = 0.0;
                foreach (int linkIndex in perElectrodeLinks)
                {
                    var link = links[linkIndex];
                    boundaryMeasure += LBUnitConverter.InputsArePhysical
                        ? link.InterfaceLengthPhys
                        : link.InterfaceLengthLU;
                }

                if (boundaryMeasure <= 0.0)
                    throw new InvalidOperationException($"Electrode {group.Key} has zero boundary measure.");

                if (LBUnitConverter.InputsArePhysical)
                {
                    double jPhys = totalCurrent / boundaryMeasure;
                    double jLu = LBUnitConverter.FluxDensityPhysToLU(jPhys);

                    foreach (int linkIndex in perElectrodeLinks)
                        flux[linkIndex] = jLu;

                    double closure = 0.0;
                    foreach (int linkIndex in perElectrodeLinks)
                        closure += jPhys * links[linkIndex].InterfaceLengthPhys;

                    double tol = Math.Max(1e-12, Math.Abs(totalCurrent) * 1e-10);
                    Debug.Assert(Math.Abs(closure - totalCurrent) <= tol,
                        $"Flux closure violated for electrode {group.Key}: expected {totalCurrent:G6}, got {closure:G6}.");
                }
                else
                {
                    double fluxDensity = totalCurrent / boundaryMeasure;

                    foreach (int linkIndex in perElectrodeLinks)
                        flux[linkIndex] = fluxDensity;

                    double closure = 0.0;
                    foreach (int linkIndex in perElectrodeLinks)
                        closure += fluxDensity * links[linkIndex].InterfaceLengthLU;

                    Debug.Assert(Math.Abs(closure - totalCurrent) <= 1e-12,
                        $"Flux closure violated for electrode {group.Key}: expected {totalCurrent:G6}, got {closure:G6}.");
                }
            }

            return flux;
        }

        /// <summary>
        /// CPU implementation of LBM forward solver using traditional object-oriented approach.
        /// Serves as reference implementation and fallback when GPU is unavailable.
        /// </summary>
        private PotentialDistribution RunForward(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            // Solver configuration replicated locally for readability.
            int maxIter = MaxIterationCount;
            double tolerance = SolutionTolerance;
            int checkFrequency = Math.Max(1, ConvergenceCheckFrequency);

            // Reconstruction updates only touch interior conductivities.  Mirror those values to the
            // ghost layer so that ghosts remain a numerical extension of the domain, not optimisation variables.
            lbmGrid.UpdateGhostConductivityFromNeighbors();

            // D2Q9 lattice constants shared with the CUDA path so that both
            // implementations evaluate exactly the same algebra.
            var weights = LatticeBoltzmannConstants.Weights;       // Equilibrium weights wi
            var opposite = LatticeBoltzmannConstants.Opposite;     // Opposite direction mapping
            // Flatten the mesh state for sequential processing.
            var elements = lbmGrid.GetElements().Cast<LBMElement>().ToList();
            int elementCount = elements.Count;

            // Map element ids to contiguous indices once; we reuse this for potential
            // lookups and to keep the CPU and GPU memory layouts identical.
            var elementIndexLookup = elements
                .Select((element, idx) => new { element.Id, idx })
                .ToDictionary(item => item.Id, item => item.idx);

            // Collect a potential distribution if one exists.  Re-using the stored
            // potential allows us to start the simulation from a steady equilibrium
            // rather than from an artificial delta function.
            var initialPotential = lbmGrid.GetPotentialDistribution();

            // Clear electrode markers before we rebuild them from the boundary data.
            foreach (var element in elements)
                element.IsElectrode = false;

            var (runtimeElectrodes, groundCellId) = BuildRuntimeElectrodeData(lbmGrid, bc, elements, elementIndexLookup);

            var boundaryTopology = lbmGrid.BoundaryTopology;
            var boundaryLinks = boundaryTopology.Links;
            var fluxPerLink = ComputeFluxPerLink(boundaryLinks, boundaryTopology.LinksByInterior, runtimeElectrodes);

            // Synchronise conductivity and initialise the discrete populations.
            var conductivity = lbmGrid.GetConductivityDistribution();
            var phi = new double[elementCount];      // Current macroscopic potential
            var prevPhi = new double[elementCount];  // Potential used for convergence checks

            static bool IsPhysicalWall(LBMElement cell) => cell.IsWall && !cell.GhostElement;

            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                bool isPhysicalWall = IsPhysicalWall(element);

                // Clamp conductivity to physical, non-negative values.  Walls are
                // treated as perfect insulators so their conductivity is zero.
                double sigmaInput = conductivity.GetConductivity(element.Id);
                double sigmaLu = LBUnitConverter.InputsArePhysical
                    ? LBUnitConverter.ConductivityPhysToLU(sigmaInput)
                    : sigmaInput;
                sigmaLu = SanitizeConductivity(sigmaLu);
                double sigma = isPhysicalWall ? 0.0 : sigmaLu;

                // Relation (a) (Krüger et al.): convert to LU before evaluating τ.
                element.Conductivity = sigma;

                if (!LBUnitConverter.InputsArePhysical || isPhysicalWall)
                    conductivity.Conductivities[element.Id] = sigma;

                // Read the stored macroscopic potential, defaulting to zero.  Every
                // non-wall cell is initialised at equilibrium: Fi = wi * φ.  Starting
                // from equilibrium avoids introducing spurious transients that could
                // destabilise the BGK relaxation during the first few iterations.
                double phi0 = 0.0;
                if (!isPhysicalWall && initialPotential is not null)
                    phi0 = initialPotential.GetValue(element.Id);

                phi[idx] = isPhysicalWall ? 0.0 : phi0;

                for (int k = 0; k < 9; k++)
                {
                    element.Fi_next[k] = 0.0;                    // Streaming buffer always starts empty.
                    element.Fi[k] = isPhysicalWall ? 0.0 : weights[k] * phi0; // Equilibrium initial state.
                }
            }
            int groundIndex = -1;
            if (groundCellId >= 0 && elementIndexLookup.TryGetValue(groundCellId, out var idxGround))
                groundIndex = idxGround;

            // Main time-stepping loop: identical ordering with the CUDA implementation.
            for (int iteration = 0; iteration < maxIter; iteration++)
            {
                // -----------------------------------------------------------------
                // 1. Collision (BGK relaxation)
                // -----------------------------------------------------------------
                foreach (var element in elements)
                {
                    if (IsPhysicalWall(element))
                        continue; // Walls keep their distributions fixed at zero.

                    double phiLocal = 0.0;
                    for (int k = 0; k < 9; k++)
                        phiLocal += element.Fi[k];

                    // Diffusion relation (a) (Krüger et al.): D = c_s^2 (τ − 1/2) Δt ⇒ τ = D / (c_s^2 Δt) + 1/2.
                    // Tau must remain greater than 0.5 to keep post-collision populations
                    // positive; we add a small safety margin to stay in the stable regime.
                    double tau = ComputeRelaxationTime(element.Conductivity);
                    double omega = 1.0 / tau;

                    for (int k = 0; k < 9; k++)
                    {
                        double feq = weights[k] * phiLocal; // Local equilibrium population.
                        element.Fi[k] += omega * (feq - element.Fi[k]);
                    }
                }

                // -----------------------------------------------------------------
                // 2. Streaming (propagate along lattice links)
                // -----------------------------------------------------------------
                foreach (var element in elements)
                {
                    if (IsPhysicalWall(element))
                        continue;

                    // Ensure the streaming buffer starts clean every iteration.
                    for (int k = 0; k < 9; k++)
                        element.Fi_next[k] = 0.0;
                }

                foreach (var element in elements)
                {
                    if (IsPhysicalWall(element))
                        continue;

                    for (int dir = 0; dir < 9; dir++)
                    {
                        double fi = element.Fi[dir];
                        var neighbour = element.Neighbors[dir];

                        bool neighbourIsPhysicalWall = neighbour is null || IsPhysicalWall(neighbour);

                        if (!neighbourIsPhysicalWall)
                        {
                            // Normal streaming: place Fi in the same directional slot
                            // of the neighbour cell.  Only one population flows through
                            // each link so a direct assignment is safe and conservative.
                            neighbour.Fi_next[dir] = fi;
                        }
                        else
                        {
                            // Bounce-back reflection: the neighbour is either outside the
                            // domain or is a wall cell, so we reverse the population into
                            // the opposite discrete velocity direction.
                            int reflected = opposite[dir];
                            element.Fi_next[reflected] = fi;
                        }
                    }
                }

                // -----------------------------------------------------------------
                // 3. Macroscopic reconstruction (φ = Σ Fi)
                // -----------------------------------------------------------------
                for (int idx = 0; idx < elementCount; idx++)
                {
                    var element = elements[idx];
                    if (IsPhysicalWall(element))
                    {
                        phi[idx] = 0.0;
                        continue;
                    }

                    double phiLocal = 0.0;
                    for (int k = 0; k < 9; k++)
                    {
                        element.Fi[k] = element.Fi_next[k]; // Swap streamed data into the main array.
                        phiLocal += element.Fi[k];
                        element.Fi_next[k] = 0.0;            // Reset buffer for next iteration.
                    }

                    phi[idx] = phiLocal;
                }

                // -----------------------------------------------------------------
                // 4. Ghost-layer boundary update (discrete Neumann flux)
                // -----------------------------------------------------------------
                ApplyGhostBoundaryConditionsCpu(elements, phi, boundaryLinks, fluxPerLink, weights, opposite);

                // Optionally monitor the sum of φ to ensure the simulation does not
                // drift.  The check is lightweight and helps spotting divergence during
                // debugging without altering the physics.
                if (iteration % checkFrequency == 0)
                {
                    double totalPhi = 0.0;
                    for (int i = 0; i < phi.Length; i++)
                        totalPhi += phi[i];
                    Debug.WriteLine($"[LBM-CPU] Iteration {iteration}: total potential = {totalPhi:G17}");
                }

                // Convergence test on the updated macroscopic field.
                if (iteration % checkFrequency == 0)
                {
                    double numerator = 0.0;
                    double denominator = 0.0;

                    for (int i = 0; i < phi.Length; i++)
                    {
                        double diff = phi[i] - prevPhi[i];
                        numerator += diff * diff;
                        denominator += phi[i] * phi[i];
                        prevPhi[i] = phi[i];
                    }

                    if (denominator > 0.0 && Math.Sqrt(numerator / denominator) < tolerance)
                        break; // Steady state reached.
                }
            }

            // Assemble the final potential distribution and push it back to the grid.
            var result = new Dictionary<int, double>(elementCount);
            double groundPhi = 0.0;
            if (groundIndex >= 0 && groundIndex < elements.Count)
                groundPhi = elements[groundIndex].Fi.Sum();
            for (int idx = 0; idx < elementCount; idx++)
            {
                double value = elements[idx].Fi.Sum();// - groundPhi;
                result[elements[idx].Id] = value;
            }

            var pd = new PotentialDistribution(result);
            lbmGrid.SetPotentialDistribution(pd);
            return pd;
        }


        private void ApplyGhostBoundaryConditionsCpu(
            IReadOnlyList<LBMElement> elements,
            double[] phi,
            IReadOnlyList<LBMBoundaryLink> links,
            double[] fluxPerLink,
            double[] weights,
            int[] opposite)
        {
            if (links.Count == 0)
                return;

            for (int i = 0; i < links.Count; i++)
            {
                var link = links[i];
                double fluxDensity = fluxPerLink.Length > i ? fluxPerLink[i] : 0.0;

                var interior = elements[link.InteriorIndex];
                var ghost = elements[link.GhostIndex];

                double phiInterior = phi[link.InteriorIndex];
                const double eps = 1e-12;
                double sigmaInterior = Math.Max(eps, interior.Conductivity);
                double sigmaGhost = ghost.Conductivity;
                if (sigmaGhost <= eps)
                {
                    sigmaGhost = Math.Max(sigmaInterior, eps); // Mirror interior conductivity when ghosts are stale.
                    ghost.Conductivity = sigmaGhost;
                }

                double sigmaAvg = 2.0 / (1.0 / sigmaInterior + 1.0 / sigmaGhost); // Harmonic mean for jump robustness.
                double dx = LatticeBoltzmannConstants.DeltaX;
                double phiGhost = phiInterior - (fluxDensity * dx) / sigmaAvg; // Relation (c): φ_g = φ_i − (j_n Δx)/σ_avg.

                for (int k = 0; k < 9; k++)
                {
                    double eq = weights[k] * phiGhost;
                    ghost.Fi[k] = eq;
                    ghost.Fi_next[k] = 0.0;
                }

                int incomingDir = opposite[link.Direction];
                double feqGhostIncoming = weights[incomingDir] * phiGhost;
                double nonEqInteriorIncoming = interior.Fi[incomingDir] - weights[incomingDir] * phiInterior;
                ghost.Fi[incomingDir] = feqGhostIncoming - nonEqInteriorIncoming; // Mirror non-equilibrium part (diffusion bounce-back).

                phi[link.GhostIndex] = phiGhost;
            }
        }

        private static (List<ElectrodeRuntimeData> RuntimeElectrodes, int GroundCellId) BuildRuntimeElectrodeData(
            LBMGrid lbmGrid,
            LBMBoundaryCondition bc,
            IList<LBMElement> elements,
            Dictionary<int, int> elementIndexLookup)
        {
            var runtimeElectrodes = new List<ElectrodeRuntimeData>();
            var processedGridIds = new HashSet<int>();

            var gridElectrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().Cast<LBMElectrode>().ToList();

            var gridElectrodesById = gridElectrodes.ToDictionary(e => e.Id, e => e);
            var gridElectrodesByGridId = gridElectrodes.ToDictionary(e => e.GridId, e => e);

            void AddRuntimeElectrode(LBMElectrode source, LBMElectrode? fallback)
            {
                if (!elementIndexLookup.TryGetValue(source.GridId, out int idx))
                {
                    if (!elementIndexLookup.TryGetValue(source.Id, out idx))
                    {
                        Debug.WriteLine($"[LBM] Electrode mapping failed (GridId={source.GridId}, Id={source.Id}). Skipping electrode.");
                        return;
                    }
                }

                var element = elements[idx];

                // Remap wall/ghost electrodes to a neighbouring interior cell to preserve fluxes.
                if (element.IsWall || element.GhostElement)
                {
                    var interiorNeighbor = element.Neighbors.FirstOrDefault(n => n != null && !n.IsWall && !n.GhostElement);
                    if (interiorNeighbor != null && elementIndexLookup.TryGetValue(interiorNeighbor.Id, out int interiorIdx))
                    {
                        element = interiorNeighbor;
                        idx = interiorIdx;
                    }
                    else
                    {
                        return;
                    }
                }

                element.IsElectrode = true;

                bool isExcitation = source.IsExcitation || (fallback?.IsExcitation ?? false);
                bool isGround = source.IsGround || (fallback?.IsGround ?? false);
                double current = double.IsNaN(source.Current) ? (fallback?.Current ?? 0.0) : source.Current;
                int electrodeId = source.Id >= 0 ? source.Id : (fallback?.Id ?? source.GridId);

                runtimeElectrodes.Add(new ElectrodeRuntimeData(electrodeId, idx, element, current, isExcitation, isGround));
                processedGridIds.Add(source.GridId);
            }

            foreach (var bcElectrode in bcElectrodes)
            {
                gridElectrodesById.TryGetValue(bcElectrode.Id, out var fallbackById);
                gridElectrodesByGridId.TryGetValue(bcElectrode.GridId, out var fallbackByGrid);
                AddRuntimeElectrode(bcElectrode, fallbackById ?? fallbackByGrid);
            }

            foreach (var gridElectrode in gridElectrodes)
            {
                if (!processedGridIds.Contains(gridElectrode.GridId))
                    AddRuntimeElectrode(gridElectrode, null);
            }

            int groundCellId = -1;
            var groundFromBc = bcElectrodes.FirstOrDefault(e => e.Id == bc.GroundElectrodeId);
            if (groundFromBc != null)
                groundCellId = groundFromBc.GridId;
            else if (gridElectrodesById.TryGetValue(bc.GroundElectrodeId, out var groundFromGrid))
                groundCellId = groundFromGrid.GridId;
            else
            {
                var runtimeGround = runtimeElectrodes.FirstOrDefault(e => e.IsGround);
                if (runtimeGround.Element != null)
                    groundCellId = runtimeGround.Element.Id;
            }

            if (groundCellId < 0 && runtimeElectrodes.Count > 0)
                groundCellId = runtimeElectrodes[0].Element.Id;

            return (runtimeElectrodes, groundCellId);
        }


        /// <summary>
        /// GPU-accelerated implementation of LBM forward solver using CUDA kernels.
        /// Processes entire mesh in parallel for maximum performance on large grids.
        /// </summary>
        private PotentialDistribution RunForwardCuda(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            EnsureCudaKernels();

            int maxIter = MaxIterationCount;
            double tolerance = SolutionTolerance;
            int checkFrequency = Math.Max(1, ConvergenceCheckFrequency);

            // Mirror interior conductivities to the ghost layer before uploading data to the GPU.
            // Ghost cells remain derived values so optimisation and reconstruction never manipulate them directly.
            lbmGrid.UpdateGhostConductivityFromNeighbors();

            var topology = LatticeBoltzmannCudaHelper.BuildTopology(lbmGrid);
            int elementCount = topology.ElementCount;
            if (elementCount == 0)
            {
                var empty = new PotentialDistribution(new Dictionary<int, double>());
                lbmGrid.SetPotentialDistribution(empty);
                return empty;
            }

            var elements = topology.Elements;
            var elementIds = topology.ElementIds;
            var isWallHost = topology.IsWall;
            var isGhostHost = topology.IsGhost;
            var neighborIndicesHost = topology.NeighborIndices;
            var neighborIsWallHost = topology.NeighborIsWall;
            var neighborIsGhostHost = topology.NeighborIsGhost;
            foreach (var element in elements)
                element.IsElectrode = false;

            var (runtimeElectrodes, groundCellId) = BuildRuntimeElectrodeData(
                lbmGrid,
                bc,
                elements,
                topology.IdToIndex);

            int groundIndex = -1;
            if (groundCellId >= 0 && topology.IdToIndex.TryGetValue(groundCellId, out var idxGround))
                groundIndex = idxGround;

            var boundaryTopology = lbmGrid.BoundaryTopology;
            var boundaryLinks = boundaryTopology.Links;
            var fluxPerLink = ComputeFluxPerLink(boundaryLinks, boundaryTopology.LinksByInterior, runtimeElectrodes);

            var conductivity = lbmGrid.GetConductivityDistribution();
            var initialPotential = lbmGrid.GetPotentialDistribution();

            var conductivityHost = new double[elementCount];
            var initialPhiHost = new double[elementCount];

            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                bool isWall = isWallHost[idx] == 1;

                double sigmaInput = conductivity.GetConductivity(element.Id);
                double sigmaLu = LBUnitConverter.InputsArePhysical
                    ? LBUnitConverter.ConductivityPhysToLU(sigmaInput)
                    : sigmaInput;
                sigmaLu = SanitizeConductivity(sigmaLu);
                double sigma = isWall ? 0.0 : sigmaLu;

                conductivityHost[idx] = sigma;

                // Relation (a) (Krüger et al.): convert to LU before the BGK step.
                element.Conductivity = sigma;

                if (!LBUnitConverter.InputsArePhysical || isWall)
                    conductivity.Conductivities[element.Id] = sigma;

                double phi0 = 0.0;
                if (!isWall && initialPotential is not null)
                    phi0 = initialPotential.GetValue(element.Id);
                initialPhiHost[idx] = isWall ? 0.0 : phi0;
            }

            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            using var fiBuffer = accelerator.Allocate1D<double>(elementCount * 9);
            using var fiNextBuffer = accelerator.Allocate1D<double>(elementCount * 9);
            using var conductivityBuffer = accelerator.Allocate1D<double>(elementCount);
            using var isWallBuffer = accelerator.Allocate1D<int>(elementCount);
            using var isGhostBuffer = accelerator.Allocate1D<int>(elementCount);
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(elementCount * 9);
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(elementCount * 9);
            using var neighborIsGhostBuffer = accelerator.Allocate1D<int>(elementCount * 9);
            using var phiBuffer = accelerator.Allocate1D<double>(elementCount);
            using var initialPhiBuffer = accelerator.Allocate1D<double>(elementCount);

            isWallBuffer.CopyFromCPU(isWallHost);
            isGhostBuffer.CopyFromCPU(isGhostHost);
            conductivityBuffer.CopyFromCPU(conductivityHost);
            neighborIndexBuffer.CopyFromCPU(neighborIndicesHost);
            neighborIsWallBuffer.CopyFromCPU(neighborIsWallHost);
            neighborIsGhostBuffer.CopyFromCPU(neighborIsGhostHost);
            initialPhiBuffer.CopyFromCPU(initialPhiHost);

            int linkCount = boundaryLinks.Count;
            using var boundaryInteriorBuffer = accelerator.Allocate1D<int>(linkCount);
            using var boundaryGhostBuffer = accelerator.Allocate1D<int>(linkCount);
            using var boundaryDirectionBuffer = accelerator.Allocate1D<int>(linkCount);
            using var boundaryFluxBuffer = accelerator.Allocate1D<double>(linkCount);

            if (linkCount > 0)
            {
                var interiorHost = boundaryLinks.Select(link => link.InteriorIndex).ToArray();
                var ghostHost = boundaryLinks.Select(link => link.GhostIndex).ToArray();
                var directionHost = boundaryLinks.Select(link => link.Direction).ToArray();

                boundaryInteriorBuffer.CopyFromCPU(interiorHost);
                boundaryGhostBuffer.CopyFromCPU(ghostHost);
                boundaryDirectionBuffer.CopyFromCPU(directionHost);
                boundaryFluxBuffer.CopyFromCPU(fluxPerLink);
            }

            if (_initializeKernel == null)
                throw new NullReferenceException();

            _initializeKernel(elementCount,
                fiBuffer.View,
                fiNextBuffer.View,
                isWallBuffer.View,
                initialPhiBuffer.View,
                LatticeBoltzmannCudaContext.WeightsView);

            accelerator.Synchronize();

            double[] prevPhi = new double[elementCount];

            if (_collisionKernel == null || _streamKernel == null || _updateKernel == null || _phiKernel == null)
                throw new NullReferenceException();

            for (int iteration = 0; iteration < maxIter; iteration++)
            {
                _collisionKernel(elementCount,
                    fiBuffer.View,
                    isWallBuffer.View,
                    conductivityBuffer.View,
                    MinTau,
                    LatticeBoltzmannConstants.DeltaT,
                    LatticeBoltzmannCudaContext.WeightsView);

                _streamKernel(elementCount,
                    fiBuffer.View,
                    fiNextBuffer.View,
                    isWallBuffer.View,
                    neighborIndexBuffer.View,
                    neighborIsWallBuffer.View,
                    LatticeBoltzmannCudaContext.OppositeView);

                var updateParams = new UpdateKernelParams(
                    fiBuffer.View,
                    fiNextBuffer.View,
                    isWallBuffer.View,
                    isGhostBuffer.View,
                    phiBuffer.View);

                _updateKernel(elementCount, updateParams);

                if (linkCount > 0 && _ghostBoundaryKernel != null)
                {
                    var ghostParams = new GhostBoundaryKernelParams(
                        boundaryInteriorBuffer.View,
                        boundaryGhostBuffer.View,
                        boundaryDirectionBuffer.View,
                        boundaryFluxBuffer.View,
                        fiBuffer.View,
                        phiBuffer.View,
                        LatticeBoltzmannCudaContext.WeightsView,
                        LatticeBoltzmannCudaContext.OppositeView,
                        conductivityBuffer.View,
                        LatticeBoltzmannConstants.DeltaX);

                    _ghostBoundaryKernel(linkCount, ghostParams);
                }

                if (iteration % checkFrequency == 0)
                {
                    accelerator.Synchronize();

                    _phiKernel(elementCount, fiBuffer.View, phiBuffer.View);
                    accelerator.Synchronize();

                    var phiHost = phiBuffer.GetAsArray1D();

                    double totalPhi = 0.0;
                    double numerator = 0.0;
                    double denominator = 0.0;

                    for (int i = 0; i < phiHost.Length; i++)
                    {
                        double value = phiHost[i];
                        double diff = value - prevPhi[i];
                        totalPhi += value;
                        numerator += diff * diff;
                        denominator += value * value;
                        prevPhi[i] = value;
                    }

                    Debug.WriteLine($"[LBM-CUDA] Iteration {iteration}: total potential = {totalPhi:G17}");

                    if (denominator > 0.0 && Math.Sqrt(numerator / denominator) < tolerance)
                        break;
                }
            }

            accelerator.Synchronize();

            var finalFi = fiBuffer.GetAsArray1D();
            double groundPhi = 0.0;
            if (groundIndex >= 0)
            {
                int groundBaseIndex = groundIndex * 9;
                for (int k = 0; k < 9; k++)
                    groundPhi += finalFi[groundBaseIndex + k];
            }

            var result = new Dictionary<int, double>(elementCount);

            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                int baseIndex = idx * 9;
                double phiValue = 0.0;

                for (int k = 0; k < 9; k++)
                {
                    double value = finalFi[baseIndex + k];
                    element.Fi[k] = value;
                    element.Fi_next[k] = 0.0;
                    phiValue += value;
                }

                result[elementIds[idx]] = phiValue;// - groundPhi;
            }

            var pd = new PotentialDistribution(result);
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
                _initializeKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<double>, ArrayView<double>>(InitializeKernel);
                _collisionKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<double>, double, double, ArrayView<double>>(CollisionKernel);
                _streamKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>>(StreamingKernel);
                _updateKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, UpdateKernelParams>(UpdateKernel);
                _ghostBoundaryKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, GhostBoundaryKernelParams>(GhostBoundaryKernel);
                _phiKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>>(PhiKernel);
            }
        }

        /// <summary>
        /// CUDA kernel for initializing distribution functions and boundary conditions.
        /// Mirrors the CPU initialisation by placing every non-wall cell in local
        /// equilibrium with its initial macroscopic potential.  Starting from equilibrium
        /// keeps the first collision step stable on both CPU and GPU implementations.
        /// </summary>
        private static void InitializeKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<double> fiNext,
            ArrayView<int> isWall,
            ArrayView<double> initialPhi,
            ArrayView<double> weights)
        {
            int baseIndex = index * 9;
            bool wall = isWall[index] == 1;
            double phi0 = wall ? 0.0 : initialPhi[index];

            for (int k = 0; k < 9; k++)
            {
                fiNext[baseIndex + k] = 0.0;                       // Streaming buffer starts empty each iteration.
                fi[baseIndex + k] = wall ? 0.0 : weights[k] * phi0; // Fluid cells begin in equilibrium with φ.
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
        /// <param name="minTau">Input: minimum relaxation time for stability</param>
        /// <param name="weights">Input: D2Q9 equilibrium weights [9]</param>
        private static void CollisionKernel(
            Index1D index,                      // Current thread's element index
            ArrayView<double> fi,               // Distribution functions to update
            ArrayView<int> isWall,              // Wall identification per element
            ArrayView<double> conductivity,     // Material conductivity per element
            double minTau,                      // Minimum relaxation time for stability
            double deltaT,                      // Time step size
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

            // Calculate relaxation time from material conductivity.
            // Relation (a): D = c_s^2 (τ − 1/2) handled centrally in ComputeTauFromDiffusivityLU.
            double tau = ComputeTauFromDiffusivityLU(conductivity[index]);

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
        /// The implementation mirrors the CPU logic to keep the collision-streaming-BC order
        /// identical across execution modes.  The kernel copies streamed populations into
        /// the main array and leaves the macroscopic gauge to be fixed in post-processing,
        /// while ghost-node kernels handle the conservative Neumann flux correction.
        /// </summary>
        private static void UpdateKernel(
            Index1D index,
            UpdateKernelParams p)
        {
            bool isPhysicalWall = p.IsWall[index] == 1 && p.IsGhost[index] == 0;
            if (isPhysicalWall)
                return;

            int baseIndex = index * 9;
            double phi = 0.0;

            for (int k = 0; k < 9; k++)
            {
                double value = p.FiNext[baseIndex + k];
                p.Fi[baseIndex + k] = value;
                p.FiNext[baseIndex + k] = 0.0;
                phi += value;
            }

            p.PhiStreamed[index] = phi;
        }

        private static void GhostBoundaryKernel(
            Index1D index,
            GhostBoundaryKernelParams p)
        {
            int interior = p.InteriorIndices[index];
            int ghost = p.GhostIndices[index];
            int direction = p.Directions[index];

            double jn = p.FluxPerLink[index];
            double phiInterior = p.Phi[interior];

            const double eps = 1e-12;
            double sigmaInterior = XMath.Max(p.Conductivity[interior], eps);
            double sigmaGhost = p.Conductivity[ghost];
            if (sigmaGhost <= eps)
            {
                sigmaGhost = sigmaInterior;
                p.Conductivity[ghost] = sigmaGhost;
            }

            double sigmaAvg = 2.0 / (1.0 / sigmaInterior + 1.0 / sigmaGhost);
            double phiGhost = phiInterior - (jn * p.DeltaX) / sigmaAvg; // Relation (c) with harmonic σ_avg.

            int baseInterior = interior * 9;
            int baseGhost = ghost * 9;

            for (int k = 0; k < 9; k++)
            {
                double eq = p.Weights[k] * phiGhost;
                p.Fi[baseGhost + k] = eq;
            }

            int incomingDir = p.Opposite[direction];
            double feqGhostIncoming = p.Weights[incomingDir] * phiGhost;
            double nonEqInteriorIncoming = p.Fi[baseInterior + incomingDir] - p.Weights[incomingDir] * phiInterior;
            p.Fi[baseGhost + incomingDir] = feqGhostIncoming - nonEqInteriorIncoming;
            p.Phi[ghost] = phiGhost;
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

#if DEBUG
        /// <summary>
        /// Debug-only acceptance test that validates current closure and CPU↔CUDA equivalence on a uniform disk
        /// while toggling diagonal boundary links.  Invoke from a debugger or immediate window when needed.
        /// </summary>
        internal static void Test_UniformDisk_CurrentClosure_And_CPUeqCUDA()
        {
            const int nx = 33;
            const double lxPhys = 0.3;      // [m]
            const double sigmaPhys = 0.5;   // [S/m]
            const double deltaTPhys = 1e-6; // [s]
            const double driveCurrent = 2e-3; // [A]

            LBUnitConverter.Configure(lxPhys, nx, sigmaPhys, deltaTPhys, inputsArePhysical: true);

            (LBMGrid Grid, LBMBoundaryCondition Boundary) CreateSetup()
            {
                var grid = new LBMGrid(nx, nx);
                grid.ApplyCircularDomain((nx - 1) / 2.0, (nx - 1) / 2.0, (nx - 3) / 2.0);

                var interior = grid.GetElements().Cast<LBMElement>()
                    .Where(e => !e.IsWall && !e.GhostElement)
                    .ToList();

                var north = interior
                    .Where(e => e.Neighbors[2]?.GhostElement == true)
                    .OrderBy(e => grid.ToLattice(e.Id).x)
                    .ToList();
                var south = interior
                    .Where(e => e.Neighbors[4]?.GhostElement == true)
                    .OrderBy(e => grid.ToLattice(e.Id).x)
                    .ToList();

                if (north.Count == 0 || south.Count == 0)
                    throw new InvalidOperationException("Circular test domain lacks boundary cells in required directions.");

                var drive = north[north.Count / 2];
                var sink = south[south.Count / 2];

                var electrodes = new List<LBMElectrode>
                {
                    new LBMElectrode(id: 0, gridId: drive.Id, current: driveCurrent, potential: 0.0, contactImpedance: 0.0, isExcitation: true, isGround: false),
                    new LBMElectrode(id: 1, gridId: sink.Id, current: -driveCurrent, potential: 0.0, contactImpedance: 0.0, isExcitation: false, isGround: true)
                };

                grid.SetElectrodes(electrodes);
                var bc = new LBMBoundaryCondition(new List<LBMElectrode>(electrodes), requireDrivePair: false);
                return (grid, bc);
            }

            void PopulateUniformConductivity(LBMGrid grid)
            {
                var conductivity = grid.GetConductivityDistribution();
                foreach (var element in grid.GetElements().Cast<LBMElement>())
                {
                    if (element.IsWall)
                        continue;

                    conductivity.Conductivities[element.Id] = sigmaPhys;
                    element.Conductivity = sigmaPhys;
                }

                grid.UpdateGhostConductivityFromNeighbors();
            }

            void ValidateFlux(LBMGrid grid, LBMBoundaryCondition bc)
            {
                var elements = grid.GetElements().Cast<LBMElement>().ToList();
                var lookup = elements
                    .Select((element, idx) => new { element.Id, idx })
                    .ToDictionary(item => item.Id, item => item.idx);

                var (runtimeElectrodes, _) = BuildRuntimeElectrodeData(grid, bc, elements, lookup);
                var topology = grid.BoundaryTopology;
                var fluxPerLink = ComputeFluxPerLink(topology.Links, topology.LinksByInterior, runtimeElectrodes);

                foreach (var group in runtimeElectrodes.GroupBy(e => e.ElectrodeId))
                {
                    var seen = new HashSet<int>();
                    double netCurrent = 0.0;

                    foreach (var electrode in group)
                    {
                        if (!topology.LinksByInterior.TryGetValue(electrode.ElementIndex, out var perCell))
                            continue;

                        foreach (int linkIndex in perCell)
                        {
                            if (!seen.Add(linkIndex))
                                continue;

                            double deltaS = LBUnitConverter.InputsArePhysical
                                ? topology.Links[linkIndex].InterfaceLengthPhys
                                : topology.Links[linkIndex].InterfaceLengthLU;
                            double fluxDensity = fluxPerLink[linkIndex];
                            if (LBUnitConverter.InputsArePhysical)
                                fluxDensity *= LBUnitConverter.DeltaXPhys / LBUnitConverter.DeltaTPhys;
                            netCurrent += fluxDensity * deltaS;
                        }
                    }

                    double expected = group.Sum(e => e.Current);
                    double tol = LBUnitConverter.InputsArePhysical
                        ? Math.Max(1e-12, Math.Abs(expected) * 1e-10)
                        : 1e-12;

                    Debug.Assert(Math.Abs(netCurrent - expected) <= tol,
                        $"Flux closure mismatch for electrode {group.Key} (diagonals {(LBMGrid.UseDiagonalBoundaryLinks ? "on" : "off")}): expected {expected:G6}, got {netCurrent:G6}.");
                }
            }

            foreach (bool useDiagonals in new[] { true, false })
            {
                bool previousToggle = LBMGrid.UseDiagonalBoundaryLinks;
                try
                {
                    LBMGrid.UseDiagonalBoundaryLinks = useDiagonals;

                    var (cpuGrid, cpuBoundary) = CreateSetup();
                    PopulateUniformConductivity(cpuGrid);
                    ValidateFlux(cpuGrid, cpuBoundary);

                    var solverCpu = new LatticeBoltzmannSolver(4000, 1e-12, 50, useCuda: false);
                    var cpuResult = solverCpu.SolveForward(cpuGrid, cpuBoundary);

                    PotentialDistribution? gpuResult = null;
                    try
                    {
                        var (gpuGrid, gpuBoundary) = CreateSetup();
                        PopulateUniformConductivity(gpuGrid);
                        var solverGpu = new LatticeBoltzmannSolver(4000, 1e-12, 50, useCuda: true);
                        gpuResult = solverGpu.SolveForward(gpuGrid, gpuBoundary);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("CUDA"))
                    {
                        continue; // CUDA unavailable in this debug session.
                    }

                    var cpuPotentials = cpuResult.Potentials;
                    var gpuPotentials = gpuResult!.Potentials;
                    double maxDiff = 0.0;

                    foreach (var element in cpuGrid.GetElements().Cast<LBMElement>())
                    {
                        if (element.IsWall || element.GhostElement)
                            continue;

                        double diff = Math.Abs(cpuPotentials[element.Id] - gpuPotentials[element.Id]);
                        maxDiff = Math.Max(maxDiff, diff);
                    }

                    Debug.Assert(maxDiff < 1e-10,
                        $"CPU/CUDA mismatch ({(useDiagonals ? "diagonals" : "axis only")}): Δφ_max = {maxDiff:G3}");
                }
                finally
                {
                    LBMGrid.UseDiagonalBoundaryLinks = previousToggle;
                }
            }
        }
#endif
    }
}
