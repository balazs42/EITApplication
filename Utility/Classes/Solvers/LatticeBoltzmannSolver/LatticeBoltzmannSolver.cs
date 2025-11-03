using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const double TauSafetyEpsilon = 1e-6;          // Small value to prevent numerical instability
        private const double MinTau = 0.5 + TauSafetyEpsilon; // Minimum relaxation time for stability
        private const double ElectrodeFluxCoefficient = 0.75; // Gentle relaxation factor for Neumann electrode corrections

        // CUDA kernel management - static to share across solver instances
        private static readonly object _cudaKernelLock = new(); // Thread-safe kernel compilation

        // Pre-compiled CUDA kernels for LBM operations
        private static System.Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<double>, ArrayView<double>>? _initializeKernel;
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
            public readonly ArrayView<int> ElectrodeIsExcitation;
            public readonly ArrayView<int> ElectrodeIsGround;
            public readonly ArrayView<double> ElectrodeCurrent;
            public readonly ArrayView<double> ElectrodePotential;
            public readonly ArrayView<int> NeighborIsWall;
            public readonly ArrayView<int> NeighborIndices;
            public readonly ArrayView<double> Conductivity;
            public readonly ArrayView<double> PhiStreamed;
            public readonly ArrayView<int> Opposite;
            public readonly ArrayView<double> Weights;
            public readonly double ElectrodeFluxAlpha;

            public UpdateKernelParams(
                ArrayView<double> fi,
                ArrayView<double> fiNext,
                ArrayView<int> isWall,
                ArrayView<int> isElectrode,
                ArrayView<int> electrodeIsExcitation,
                ArrayView<int> electrodeIsGround,
                ArrayView<double> electrodeCurrent,
                ArrayView<double> electrodePotential,
                ArrayView<int> neighborIsWall,
                ArrayView<int> neighborIndices,
                ArrayView<double> conductivity,
                ArrayView<double> phiStreamed,
                ArrayView<int> opposite,
                ArrayView<double> weights,
                double electrodeFluxAlpha)
            {
                Fi = fi;
                FiNext = fiNext;
                IsWall = isWall;
                IsElectrode = isElectrode;
                ElectrodeIsExcitation = electrodeIsExcitation;
                ElectrodeIsGround = electrodeIsGround;
                ElectrodeCurrent = electrodeCurrent;
                ElectrodePotential = electrodePotential;
                NeighborIsWall = neighborIsWall;
                NeighborIndices = neighborIndices;
                Conductivity = conductivity;
                PhiStreamed = phiStreamed;
                Opposite = opposite;
                Weights = weights;
                ElectrodeFluxAlpha = electrodeFluxAlpha;
            }
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
            public ElectrodeRuntimeData(int elementIndex, LBMElement element, double current, double potential, bool isExcitation, bool isGround)
            {
                ElementIndex = elementIndex;
                Element = element;
                Current = current;
                Potential = potential;
                IsExcitation = isExcitation;
                IsGround = isGround;
            }

            public int ElementIndex { get; }
            public LBMElement Element { get; }
            public double Current { get; }
            public double Potential { get; }
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
        /// CPU implementation of LBM forward solver using traditional object-oriented approach.
        /// Serves as reference implementation and fallback when GPU is unavailable.
        /// </summary>
        private PotentialDistribution RunForward(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            // Solver configuration replicated locally for readability.
            int maxIter = MaxIterationCount;
            double tolerance = SolutionTolerance;
            int checkFrequency = Math.Max(1, ConvergenceCheckFrequency);

            // D2Q9 lattice constants shared with the CUDA path so that both
            // implementations evaluate exactly the same algebra.
            var weights = LatticeBoltzmannConstants.Weights;       // Equilibrium weights wi
            var opposite = LatticeBoltzmannConstants.Opposite;     // Opposite direction mapping
            double csSquared = LatticeBoltzmannConstants.CsSquared; // c_s^2 for diffusion relation

            // Flatten the mesh state for sequential processing.
            var elements = lbmGrid.GetElements().Cast<LBMElement>().ToList();
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().Cast<LBMElectrode>().ToList();
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

            // Merge electrode information coming from the discretization (currents
            // for the drive pattern) and from the boundary condition description.
            var gridElectrodesById = electrodes.ToDictionary(e => e.Id, e => e);
            var gridElectrodesByGridId = electrodes.ToDictionary(e => e.GridId, e => e);
            var runtimeElectrodes = new List<ElectrodeRuntimeData>();
            var processedGridIds = new HashSet<int>();

            void AddRuntimeElectrode(LBMElectrode source, LBMElectrode? fallback)
            {
                if (!elementIndexLookup.TryGetValue(source.GridId, out int idx))
                    return; // Electrode references a cell that is not part of the grid.

                var element = elements[idx];
                element.IsElectrode = true; // Mark for boundary condition enforcement.

                // Combine flags in a conservative OR fashion so that either
                // description (grid or boundary) can request excitation/ground roles.
                bool isExcitation = source.IsExcitation || (fallback?.IsExcitation ?? false);
                bool isGround = source.IsGround || (fallback?.IsGround ?? false);

                // Prefer the explicitly supplied currents/potentials, but fall back
                // to the secondary description if they are unavailable (NaN) to keep
                // legacy boundary files working.
                double current = double.IsNaN(source.Current) ? (fallback?.Current ?? 0.0) : source.Current;
                double potential = double.IsNaN(source.Potential) ? (fallback?.Potential ?? 0.0) : source.Potential;

                runtimeElectrodes.Add(new ElectrodeRuntimeData(idx, element, current, potential, isExcitation, isGround));
                processedGridIds.Add(source.GridId);
            }

            foreach (var bcElectrode in bcElectrodes)
            {
                gridElectrodesById.TryGetValue(bcElectrode.Id, out var fallbackById);
                gridElectrodesByGridId.TryGetValue(bcElectrode.GridId, out var fallbackByGrid);
                AddRuntimeElectrode(bcElectrode, fallbackById ?? fallbackByGrid);
            }

            foreach (var gridElectrode in electrodes)
            {
                if (!processedGridIds.Contains(gridElectrode.GridId))
                    AddRuntimeElectrode(gridElectrode, null);
            }

            // Synchronise conductivity and initialise the discrete populations.
            var conductivity = lbmGrid.GetConductivityDistribution();
            var phi = new double[elementCount];      // Current macroscopic potential
            var prevPhi = new double[elementCount];  // Potential used for convergence checks

            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                bool isWall = element.IsWall;

                // Clamp conductivity to physical, non-negative values.  Walls are
                // treated as perfect insulators so their conductivity is zero.
                double sigma = isWall ? 0.0 : SanitizeConductivity(conductivity.GetConductivity(element.Id));
                element.Conductivity = sigma;
                conductivity.Conductivities[element.Id] = sigma;

                // Read the stored macroscopic potential, defaulting to zero.  Every
                // non-wall cell is initialised at equilibrium: Fi = wi * φ.  Starting
                // from equilibrium avoids introducing spurious transients that could
                // destabilise the BGK relaxation during the first few iterations.
                double phi0 = 0.0;
                if (!isWall && initialPotential is not null)
                    phi0 = initialPotential.GetValue(element.Id);

                phi[idx] = isWall ? 0.0 : phi0;

                for (int k = 0; k < 9; k++)
                {
                    element.Fi_next[k] = 0.0;                    // Streaming buffer always starts empty.
                    element.Fi[k] = isWall ? 0.0 : weights[k] * phi0; // Equilibrium initial state.
                }
            }

            // Main time-stepping loop: identical ordering with the CUDA implementation.
            for (int iteration = 0; iteration < maxIter; iteration++)
            {
                // -----------------------------------------------------------------
                // 1. Collision (BGK relaxation)
                // -----------------------------------------------------------------
                foreach (var element in elements)
                {
                    if (element.IsWall)
                        continue; // Walls keep their distributions fixed at zero.

                    double phiLocal = 0.0;
                    for (int k = 0; k < 9; k++)
                        phiLocal += element.Fi[k];

                    // Diffusion relation: tau = D / c_s^2 + 0.5.  Tau must remain
                    // greater than 0.5 to keep the post-collision populations
                    // positive; we add a small safety margin to stay in the stable
                    // regime of the discrete BGK operator.
                    double tau = ComputeRelaxationTime(element.Conductivity, csSquared);
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
                    if (element.IsWall)
                        continue;

                    // Ensure the streaming buffer starts clean every iteration.
                    for (int k = 0; k < 9; k++)
                        element.Fi_next[k] = 0.0;
                }

                foreach (var element in elements)
                {
                    if (element.IsWall)
                        continue;

                    for (int dir = 0; dir < 9; dir++)
                    {
                        double fi = element.Fi[dir];
                        var neighbour = element.Neighbors[dir];

                        if (neighbour is not null && !neighbour.IsWall)
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
                    if (element.IsWall)
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
                // 4. Electrode boundary conditions (Neumann + Dirichlet anchor)
                // -----------------------------------------------------------------
                ApplyElectrodeBoundaryConditionsCpu(runtimeElectrodes, phi, elementIndexLookup, weights, opposite);

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
            for (int idx = 0; idx < elementCount; idx++)
                result[elements[idx].Id] = elements[idx].Fi.Sum();

            var pd = new PotentialDistribution(result);
            lbmGrid.SetPotentialDistribution(pd);
            return pd;
        }


        /// <summary>
        /// Applies the mixed Neumann/Dirichlet electrode boundary conditions on the
        /// CPU implementation.  The routine mirrors the CUDA kernel logic so that both
        /// execution paths remain mathematically identical.
        /// </summary>
        private void ApplyElectrodeBoundaryConditionsCpu(
            IReadOnlyList<ElectrodeRuntimeData> electrodes,
            double[] phi,
            IReadOnlyDictionary<int, int> elementIndexLookup,
            double[] weights,
            int[] opposite)
        {
            // Same relaxation factor as used in the CUDA kernels.
            const double alpha = ElectrodeFluxCoefficient;

            foreach (var electrode in electrodes)
            {
                var element = electrode.Element;
                int elementIndex = electrode.ElementIndex;

                // ---------------------------------------------------------------------
                // 1. Dirichlet anchor for ground electrode (pins the gauge)
                // ---------------------------------------------------------------------
                double phiBoundary = phi[elementIndex];

                if (electrode.IsGround)
                {
                    double anchorPhi = electrode.Potential;

                    // Overwrite all populations with equilibrium at the ground potential.
                    for (int k = 0; k < 9; k++)
                        element.Fi[k] = weights[k] * anchorPhi;

                    phiBoundary = anchorPhi;
                    phi[elementIndex] = anchorPhi;
                }

                // ---------------------------------------------------------------------
                // 2. Find outward normal direction: link to a wall with fluid on the
                //    opposite side (i.e. a wall-interior pair). Prefer cardinal over
                //    diagonal directions, just like in the CUDA UpdateKernel.
                // ---------------------------------------------------------------------
                int outward = -1;

                for (int dir = 1; dir < 9; dir++) // skip rest direction 0
                {
                    var nb = element.Neighbors[dir];
                    if (nb is null || !nb.IsWall)
                        continue;

                    int inwardCandidate = opposite[dir];
                    var interiorCand = element.Neighbors[inwardCandidate];

                    // Need a true interior fluid cell behind the boundary face
                    if (interiorCand is null || interiorCand.IsWall)
                        continue;

                    // First acceptable direction, or prefer cardinal (1–4) over diagonal (5–8)
                    if (outward < 0 || (dir < 5 && outward >= 5))
                        outward = dir;
                }

                if (outward < 0)
                    continue; // No clear wall normal -> nothing to correct.

                int inward = opposite[outward];
                var interior = element.Neighbors[inward];

                if (interior is null || interior.IsWall)
                    continue; // No interior fluid cell to exchange flux with.

                if (!elementIndexLookup.TryGetValue(interior.Id, out int interiorIndex))
                    continue;

                // ---------------------------------------------------------------------
                // 3. Discrete Neumann flux on that link
                // ---------------------------------------------------------------------
                double phiInterior = phi[interiorIndex];
                double phiBoundaryCell = phi[elementIndex]; // may have been anchored above

                double sigmaBoundary = element.Conductivity;
                double sigmaInterior = interior.Conductivity;
                double sigmaAvg = 0.5 * (sigmaBoundary + sigmaInterior);

                if (sigmaAvg <= 0.0)
                    continue; // Perfect insulator -> no flux through this face.

                // Discrete normal flux along the inward link:
                //   j_n ≈ sigmaAvg * (phiInterior - phiBoundaryCell)
                //
                // Then superimpose the prescribed electrode current (per cell) in the
                // same sign convention.  Positive electrode.Current increases inward
                // flux, negative decreases it.
                double flux = sigmaAvg * (phiInterior - phiBoundaryCell) + Math.Abs(electrode.Current);
                double deltaFi = alpha * flux * weights[inward];

                // ---------------------------------------------------------------------
                // 4. Conservative population correction (modified bounce-back)
                // ---------------------------------------------------------------------
                if (electrode.IsExcitation)
                {
                    // Source electrode: inject current into the domain; bias the inward
                    // population and counter-bias the outward one to keep ΣFi constant.
                    element.Fi[inward] += deltaFi;
                    element.Fi[outward] -= deltaFi;
                }
                else if (electrode.IsGround)
                {
                    // Ground electrode acting as sink: reverse the flux direction while
                    // still preserving the local population sum.
                    element.Fi[inward] -= deltaFi;
                    element.Fi[outward] += deltaFi;
                }
                else
                {
                    // Floating / measurement electrode: only the potential anchor (if
                    // any) is enforced, no additional flux correction.
                    continue;
                }

                // ---------------------------------------------------------------------
                // 5. Update cached macroscopic potential at the electrode cell so that
                //    subsequent iterations and convergence check see the corrected φ.
                // ---------------------------------------------------------------------
                double updatedPhi = 0.0;
                for (int k = 0; k < 9; k++)
                    updatedPhi += element.Fi[k];

                phi[elementIndex] = updatedPhi;
            }
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
            var neighborIndicesHost = topology.NeighborIndices;
            var neighborIsWallHost = topology.NeighborIsWall;

            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToArray();
            var bcElectrodes = bc.GetElectrodes().Cast<LBMElectrode>().ToArray();

            var gridElectrodesById = electrodes.ToDictionary(e => e.Id, e => e);
            var gridElectrodesByGridId = electrodes.ToDictionary(e => e.GridId, e => e);
            var runtimeElectrodes = new List<ElectrodeRuntimeData>();
            var processedGridIds = new HashSet<int>();

            void AddRuntimeElectrode(LBMElectrode source, LBMElectrode? fallback)
            {
                if (!topology.IdToIndex.TryGetValue(source.GridId, out int idx))
                    return;

                var element = elements[idx];
                element.IsElectrode = true;

                bool isExcitation = source.IsExcitation || (fallback?.IsExcitation ?? false);
                bool isGround = source.IsGround || (fallback?.IsGround ?? false);

                double current = double.IsNaN(source.Current) ? (fallback?.Current ?? 0.0) : source.Current;
                double potential = double.IsNaN(source.Potential) ? (fallback?.Potential ?? 0.0) : source.Potential;

                runtimeElectrodes.Add(new ElectrodeRuntimeData(idx, element, current, potential, isExcitation, isGround));
                processedGridIds.Add(source.GridId);
            }

            foreach (var bcElectrode in bcElectrodes)
            {
                gridElectrodesById.TryGetValue(bcElectrode.Id, out var fallbackById);
                gridElectrodesByGridId.TryGetValue(bcElectrode.GridId, out var fallbackByGrid);
                AddRuntimeElectrode(bcElectrode, fallbackById ?? fallbackByGrid);
            }

            foreach (var gridElectrode in electrodes)
            {
                if (!processedGridIds.Contains(gridElectrode.GridId))
                    AddRuntimeElectrode(gridElectrode, null);
            }

            var electrodeByIndex = runtimeElectrodes.ToDictionary(e => e.ElementIndex);
            var conductivity = lbmGrid.GetConductivityDistribution();
            var initialPotential = lbmGrid.GetPotentialDistribution();

            var isElectrodeHost = new int[elementCount];
            var electrodeIsExcitationHost = new int[elementCount];
            var electrodeIsGroundHost = new int[elementCount];
            var electrodeCurrentHost = new double[elementCount];
            var electrodePotentialHost = new double[elementCount];
            var conductivityHost = new double[elementCount];
            var initialPhiHost = new double[elementCount];

            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                bool isWall = isWallHost[idx] == 1;

                double sigma = isWall ? 0.0 : SanitizeConductivity(conductivity.GetConductivity(element.Id));
                conductivityHost[idx] = sigma;
                element.Conductivity = sigma;
                conductivity.Conductivities[element.Id] = sigma;

                double phi0 = 0.0;
                if (!isWall && initialPotential is not null)
                    phi0 = initialPotential.GetValue(element.Id);
                initialPhiHost[idx] = isWall ? 0.0 : phi0;

                if (electrodeByIndex.TryGetValue(idx, out var runtimeElectrode))
                {
                    isElectrodeHost[idx] = 1;
                    electrodeIsExcitationHost[idx] = runtimeElectrode.IsExcitation ? 1 : 0;
                    electrodeIsGroundHost[idx] = runtimeElectrode.IsGround ? 1 : 0;
                    electrodeCurrentHost[idx] = runtimeElectrode.Current;
                    electrodePotentialHost[idx] = runtimeElectrode.Potential;
                }
                else
                {
                    element.IsElectrode = false;
                    isElectrodeHost[idx] = 0;
                    electrodeIsExcitationHost[idx] = 0;
                    electrodeIsGroundHost[idx] = 0;
                    electrodeCurrentHost[idx] = 0.0;
                    electrodePotentialHost[idx] = 0.0;
                }
            }

            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            using var fiBuffer = accelerator.Allocate1D<double>(elementCount * 9);
            using var fiNextBuffer = accelerator.Allocate1D<double>(elementCount * 9);
            using var conductivityBuffer = accelerator.Allocate1D<double>(elementCount);
            using var isWallBuffer = accelerator.Allocate1D<int>(elementCount);
            using var isElectrodeBuffer = accelerator.Allocate1D<int>(elementCount);
            using var electrodeIsExcitationBuffer = accelerator.Allocate1D<int>(elementCount);
            using var electrodeIsGroundBuffer = accelerator.Allocate1D<int>(elementCount);
            using var electrodeCurrentBuffer = accelerator.Allocate1D<double>(elementCount);
            using var electrodePotentialBuffer = accelerator.Allocate1D<double>(elementCount);
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(elementCount * 9);
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(elementCount * 9);
            using var phiBuffer = accelerator.Allocate1D<double>(elementCount);
            using var initialPhiBuffer = accelerator.Allocate1D<double>(elementCount);

            isWallBuffer.CopyFromCPU(isWallHost);
            isElectrodeBuffer.CopyFromCPU(isElectrodeHost);
            electrodeIsExcitationBuffer.CopyFromCPU(electrodeIsExcitationHost);
            electrodeIsGroundBuffer.CopyFromCPU(electrodeIsGroundHost);
            electrodeCurrentBuffer.CopyFromCPU(electrodeCurrentHost);
            electrodePotentialBuffer.CopyFromCPU(electrodePotentialHost);
            conductivityBuffer.CopyFromCPU(conductivityHost);
            neighborIndexBuffer.CopyFromCPU(neighborIndicesHost);
            neighborIsWallBuffer.CopyFromCPU(neighborIsWallHost);
            initialPhiBuffer.CopyFromCPU(initialPhiHost);

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
                    LatticeBoltzmannConstants.CsSquared,
                    MinTau,
                    LatticeBoltzmannCudaContext.WeightsView);

                _streamKernel(elementCount,
                    fiBuffer.View,
                    fiNextBuffer.View,
                    isWallBuffer.View,
                    neighborIndexBuffer.View,
                    neighborIsWallBuffer.View,
                    LatticeBoltzmannCudaContext.OppositeView);

                _phiKernel(elementCount, fiNextBuffer.View, phiBuffer.View);

                var updateParams = new UpdateKernelParams(
                    fiBuffer.View,
                    fiNextBuffer.View,
                    isWallBuffer.View,
                    isElectrodeBuffer.View,
                    electrodeIsExcitationBuffer.View,
                    electrodeIsGroundBuffer.View,
                    electrodeCurrentBuffer.View,
                    electrodePotentialBuffer.View,
                    neighborIsWallBuffer.View,
                    neighborIndexBuffer.View,
                    conductivityBuffer.View,
                    phiBuffer.View,
                    LatticeBoltzmannCudaContext.OppositeView,
                    LatticeBoltzmannCudaContext.WeightsView,
                    ElectrodeFluxCoefficient);

                _updateKernel(elementCount, updateParams);

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

                result[elementIds[idx]] = phiValue;
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
        /// The implementation mirrors the CPU logic to keep the collision-streaming-BC order
        /// identical across execution modes.  The kernel copies streamed populations into
        /// the main array, applies the Dirichlet anchor on ground electrodes, and finally
        /// injects the conservative Neumann flux correction for source/sink electrodes.
        /// </summary>
        private static void UpdateKernel(
            Index1D index,
            UpdateKernelParams p)
        {
            if (p.IsWall[index] == 1)
                return;

            int baseIndex = index * 9;
            double phiBoundary = 0.0;

            for (int k = 0; k < 9; k++)
            {
                double value = p.FiNext[baseIndex + k];
                p.Fi[baseIndex + k] = value;   // Accept streamed population.
                p.FiNext[baseIndex + k] = 0.0; // Clear buffer for the next iteration.
                phiBoundary += value;
            }

            if (p.IsElectrode[index] == 0)
            {
                p.PhiStreamed[index] = phiBoundary;
                return;
            }

            if (p.ElectrodeIsGround[index] == 1)
            {
                double anchorPotential = p.ElectrodePotential[index];
                phiBoundary = 0.0;
                for (int k = 0; k < 9; k++)
                {
                    double value = p.Weights[k] * anchorPotential;
                    p.Fi[baseIndex + k] = value; // Enforce Dirichlet equilibrium.
                    phiBoundary += value;
                }
                p.PhiStreamed[index] = phiBoundary;
            }
            else
            {
                p.PhiStreamed[index] = phiBoundary;
            }

            int outwardDirection = -1;
            for (int dir = 1; dir < 9; dir++)
            {
                if (p.NeighborIsWall[baseIndex + dir] != 1)
                    continue;

                int inward = p.Opposite[dir];
                if (p.NeighborIsWall[baseIndex + inward] == 1)
                    continue;

                if (outwardDirection < 0 || (dir < 5 && outwardDirection >= 5))
                    outwardDirection = dir;
            }

            if (outwardDirection < 0)
                return;

            int inwardDirection = p.Opposite[outwardDirection];
            int interiorIndex = p.NeighborIndices[baseIndex + inwardDirection];
            if (interiorIndex < 0)
                return;
            if (p.IsWall[interiorIndex] == 1)
                return;

            double phiInterior = p.PhiStreamed[interiorIndex];
            double phiBoundaryLocal = p.PhiStreamed[index];

            double sigmaBoundary = p.Conductivity[index];
            double sigmaInterior = p.Conductivity[interiorIndex];
            double sigmaAvg = 0.5 * (sigmaBoundary + sigmaInterior);
            if (sigmaAvg <= 0.0)
                return;

            double flux = sigmaAvg * (phiInterior - phiBoundaryLocal) + p.ElectrodeCurrent[index];
            double deltaFi = p.ElectrodeFluxAlpha * flux * p.Weights[inwardDirection];

            if (p.ElectrodeIsExcitation[index] == 1)
            {
                p.Fi[baseIndex + inwardDirection] += deltaFi;
                p.Fi[baseIndex + outwardDirection] -= deltaFi;
            }
            else if (p.ElectrodeIsGround[index] == 1)
            {
                p.Fi[baseIndex + inwardDirection] -= deltaFi;
                p.Fi[baseIndex + outwardDirection] += deltaFi;
            }
            else
            {
                return;
            }

            double phiUpdated = 0.0;
            for (int k = 0; k < 9; k++)
                phiUpdated += p.Fi[baseIndex + k];
            p.PhiStreamed[index] = phiUpdated;
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
