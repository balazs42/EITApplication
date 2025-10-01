using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// LBM-based solver for solving the diffusion PDE ∇·(γ∇φ)=f via D2Q9 lattice.
    /// Implements collision, streaming, bounce-back, and CEM boundary directly.
    /// Only uses LatticeBoltzmannOperators for inverse finite-difference gradient.
    /// </summary>
    public sealed class LatticeBoltzmannSolver : ISolver
    {
        private int MaxIterationCount = 250;
        private double SolutionTolerance = 1e-6;
        private int ConvergenceCheckFrequency = 100;
        private readonly bool _useCuda;

        private const double TauSafetyEpsilon = 1e-6;
        private const double MinTau = 0.5 + TauSafetyEpsilon;

        private static readonly object _cudaKernelLock = new();
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _initializeKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<double>, double, double, ArrayView<double>>? _collisionKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>>? _streamKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>? _updateKernel;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>>? _phiKernel;

        /// <summary>
        /// Configures the LBM solver with iteration limits and convergence criteria.
        /// </summary>
        public LatticeBoltzmannSolver(int maxIterationCount, double solutionTolerance, int convergenceCheckFrequency, bool useCuda = false)
        {
            MaxIterationCount = maxIterationCount;
            SolutionTolerance = solutionTolerance;
            ConvergenceCheckFrequency = convergenceCheckFrequency;
            _useCuda = useCuda;
        }

        private static double SanitizeConductivity(double conductivity)
        {
            if (double.IsNaN(conductivity) || double.IsInfinity(conductivity))
                return 0.0;

            return Math.Max(0.0, conductivity);
        }

        private static double ComputeRelaxationTime(double conductivity, double csSquared)
        {
            double tau = conductivity / csSquared + 0.5;
            return tau < MinTau ? MinTau : tau;
        }

        /// <summary>
        /// Solves the forward diffusion problem on the LBM grid using the configured parameters.
        /// </summary>
        public PotentialDistribution SolveForward(IDiscretization discretization, BoundaryCondition boundaryCondition)
        {
            var lbmGrid = discretization as LBMGrid ?? throw new InvalidCastException();
            var bc = boundaryCondition as LBMBoundaryCondition ?? throw new InvalidCastException();

            return _useCuda ? RunForwardCuda(lbmGrid, bc) : RunForward(lbmGrid, bc);
        }

        /// <summary>
        /// Reuses the forward time stepping to solve the adjoint LBM problem driven by electrode sources.
        /// </summary>
        public PotentialDistribution SolveAdjoint(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[] adjointSource)
        {
            var lbmGrid = discretization as LBMGrid ?? throw new InvalidCastException();
            var bc = boundaryCondition as LBMBoundaryCondition ?? throw new InvalidCastException();
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().ToList();
            int bcElectrodeCount = bcElectrodes.Count();

            for(int i = 0; i < bcElectrodeCount; i++)
            {
                bcElectrodes[i].Potential = 0.0;
                electrodes[i].Potential = 0.0;

                bcElectrodes[i].Current = adjointSource[i].Real;   // TODO: add complex currents
                electrodes[i].Current = adjointSource[i].Real;
            }

            return _useCuda ? RunForwardCuda(lbmGrid, bc) : RunForward(lbmGrid, bc);
        }

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
        /// Runs the forward LBM until steady-state, returning electrode potentials.
        /// </summary>
        private PotentialDistribution RunForward(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            int maxIter = MaxIterationCount;
            double tol = SolutionTolerance;
            int checkFreq = ConvergenceCheckFrequency;

            var weights = LatticeBoltzmannConstants.Weights;
            var opposite = LatticeBoltzmannConstants.Opposite;
            double csSquared = LatticeBoltzmannConstants.CsSquared;

            var elements = lbmGrid.GetElements().Cast<LBMElement>().ToList();
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().ToList();

            // 1) Initialize distributions Fi and Fi_next to zero
            foreach (var el in elements)
            {
                for (int k = 0; k < 9; k++)
                {
                    el.Fi[k] = weights[k];        // equilibrium with φ=1
                    el.Fi_next[k] = 0.0;
                }
                if(el.IsElectrode)
                {
                    var correspondingElectrode = electrodes.Find(x => x.GridId == el.Id);

                    if (correspondingElectrode != null)
                    {
                        if(correspondingElectrode.IsExcitation || correspondingElectrode.IsGround)
                        {
                            // Set the current going out of the electrode
                            double current = correspondingElectrode.Current;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = weights[i] * current;

                            // Reverse the directions which would go into walls
                            // TODO: Ground electrode should point outsidde?,
                            var neighbors = el.Neighbors;
                            for (int i = 0; i < 9; i++)
                            {
                                if (neighbors[i].IsWall)
                                {
                                    el.Fi[opposite[i]] += el.Fi[i];
                                    el.Fi[i] = 0.0;
                                }
                            }
                        }
                        else // Prescribe the potential onn the electrodes
                        {
                            correspondingElectrode.Potential = bcElectrodes[correspondingElectrode.Id].Potential;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = weights[i] * correspondingElectrode.Potential;
                        }

                    }
                }
            }

            // 2) Load conductivity γ into each element
            var sigmaDist = lbmGrid.GetConductivityDistribution();
            foreach (var el in elements)
                el.Conductivity = SanitizeConductivity(sigmaDist.GetConductivity(el.Id));

            // 3) Mark electrodes as pinned Dirichlet
            foreach (var electrode in bcElectrodes)
            {
                var cell = elements.First(e => e.Id == electrode.GridId);
                if (cell != null)
                    cell.IsElectrode = true;
            }

            // 4) Main loop
            double[] prevPhi = new double[elements.Count()];
            for (int t = 0; t < maxIter; t++)
            {
                // 4a) Collision
                foreach (var el in elements)
                {
                    if (el.IsWall)
                        continue;

                    // Macroscopic phi = sum Fi
                    double phi = 0;
                    for (int k = 0; k < 9; k++)
                        phi += el.Fi[k];

                    // Relaxation time τ = D / cs^2 + 0.5, D = γ
                    double tau = ComputeRelaxationTime(el.Conductivity, csSquared);
                    double omega = 1.0 / tau;

                    // BGK collision towards equilibrium geq = W[k]*phi (thesis eq. 4.3.1)
                    for (int k = 0; k < 9; k++)
                    {
                        double geq = weights[k] * phi;
                        el.Fi[k] += -omega * (el.Fi[k] - geq);
                    }
                }

                // 4b) Streaming + bounce-back
                foreach (var el in elements)
                {
                    if (el.IsWall)
                        continue;

                    for (int k = 0; k < 9; k++)
                    {
                        var nb = el.Neighbors[k];

                        // send to the same direction slot in neighbor
                        if (!nb.IsWall)
                            nb.Fi_next[k] = el.Fi[k];

                        // bounce-back into opposite direction
                        else
                            el.Fi_next[opposite[k]] = el.Fi[k];
                    }
                }

                // 4c) Update and enforce pins
                foreach (var el in elements)
                {
                    if (el.IsWall)
                        continue;

                    // copy next to current
                    for (int k = 0; k < 9; k++)
                    {
                        el.Fi[k] = el.Fi_next[k];
                        el.Fi_next[k] = 0.0;
                    }
                    // enforce boundary condtion Neumann or Dirichlet: Fi = W[k]*PinValue
                    if (el.IsElectrode)
                    {
                        var electrode = electrodes.Find(x => x.GridId == el.Id) ?? throw new ArgumentNullException("Cannot find electrode with specified id!");
                        double potential = electrode.Potential;
                        // Neumann
                        if(electrode.IsExcitation || electrode.IsGround)
                        {
                            double current = electrode.Current;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = weights[i] * current;

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
                        // Dirichlet
                        else
                        {
                            for (int k = 0; k < 9; k++)
                                el.Fi[k] = weights[k] * potential;
                        }
                    }
                }

                // 4d) Convergence check every checkFreq steps
                if (t % checkFreq == 0)
                {
                    var phi = elements.Select(e => e.Fi.Sum()).ToArray();

                    double num = 0, den = 0;

                    for (int i = 0; i < phi.Length; i++)
                    {
                        double d = phi[i] - prevPhi[i];
                        num += d * d;
                        den += phi[i] * phi[i];
                    }

                    if (den > 0 && Math.Sqrt(num / den) < tol)
                        break;
                    Array.Copy(phi, prevPhi, phi.Length);
                }
            }

            // Set mesh variables
            var dict = new Dictionary<int, double>();

            foreach (var elemenet in elements)
                dict.Add(elemenet.Id, elemenet.Fi.Sum());

            var pd = new PotentialDistribution(dict);

            lbmGrid.SetPotentialDistribution(pd);

            return pd;
        }

        private PotentialDistribution RunForwardCuda(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            EnsureCudaKernels();

            int maxIter = MaxIterationCount;
            double tol = SolutionTolerance;
            int checkFreq = ConvergenceCheckFrequency;

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

            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToArray();
            var bcElectrodes = bc.GetElectrodes().Cast<LBMElectrode>().ToArray();

            var electrodeByGridId = electrodes.ToDictionary(e => e.GridId);
            var bcElectrodeById = bcElectrodes.ToDictionary(e => e.Id);
            var bcElectrodeByGridId = bcElectrodes.ToDictionary(e => e.GridId);

            var isWallHost = topology.IsWall;
            var neighborIndicesHost = topology.NeighborIndices;
            var neighborIsWallHost = topology.NeighborIsWall;

            var isElectrodeHost = new int[elementCount];
            var electrodeIsSourceHost = new int[elementCount];
            var electrodeCurrentHost = new double[elementCount];
            var electrodePotentialHost = new double[elementCount];
            var conductivityHost = new double[elementCount];

            var sigmaDist = lbmGrid.GetConductivityDistribution();

            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                double conductivity = SanitizeConductivity(sigmaDist.GetConductivity(element.Id));
                element.Conductivity = conductivity;
                conductivityHost[idx] = conductivity;

                bool isElectrode = element.IsElectrode;
                double electrodeCurrent = 0.0;
                double electrodePotential = 0.0;
                int isSource = 0;

                if (electrodeByGridId.TryGetValue(element.Id, out var electrode))
                {
                    isElectrode = true;
                    electrodeCurrent = electrode.Current;
                    if (electrode.IsExcitation || electrode.IsGround)
                    {
                        isSource = 1;
                    }
                    else
                    {
                        if (bcElectrodeById.TryGetValue(electrode.Id, out var bcElectrode))
                            electrodePotential = bcElectrode.Potential;
                        else if (bcElectrodeByGridId.TryGetValue(element.Id, out var bcElectrodeByGrid))
                            electrodePotential = bcElectrodeByGrid.Potential;
                        else
                            electrodePotential = electrode.Potential;
                    }
                }
                else if (bcElectrodeByGridId.TryGetValue(element.Id, out var bcElectrode))
                {
                    isElectrode = true;
                    electrodePotential = bcElectrode.Potential;
                }

                element.IsElectrode = isElectrode;

                isElectrodeHost[idx] = isElectrode ? 1 : 0;
                electrodeIsSourceHost[idx] = isSource;
                electrodeCurrentHost[idx] = electrodeCurrent;
                electrodePotentialHost[idx] = electrodePotential;
            }

            for (int idx = 0; idx < elementCount; idx++)
            {
                conductivityHost[idx] = SanitizeConductivity(conductivityHost[idx]);
            }

            var accelerator = LatticeBoltzmannCudaContext.Accelerator;

            using var fiBuffer = accelerator.Allocate1D<double>(elementCount * 9);
            using var fiNextBuffer = accelerator.Allocate1D<double>(elementCount * 9);
            using var conductivityBuffer = accelerator.Allocate1D<double>(elementCount);
            using var isWallBuffer = accelerator.Allocate1D<int>(elementCount);
            using var isElectrodeBuffer = accelerator.Allocate1D<int>(elementCount);
            using var electrodeIsSourceBuffer = accelerator.Allocate1D<int>(elementCount);
            using var electrodeCurrentBuffer = accelerator.Allocate1D<double>(elementCount);
            using var electrodePotentialBuffer = accelerator.Allocate1D<double>(elementCount);
            using var neighborIndexBuffer = accelerator.Allocate1D<int>(elementCount * 9);
            using var neighborIsWallBuffer = accelerator.Allocate1D<int>(elementCount * 9);
            using var phiBuffer = accelerator.Allocate1D<double>(elementCount);

            isWallBuffer.CopyFromCPU(isWallHost);
            isElectrodeBuffer.CopyFromCPU(isElectrodeHost);
            electrodeIsSourceBuffer.CopyFromCPU(electrodeIsSourceHost);
            electrodeCurrentBuffer.CopyFromCPU(electrodeCurrentHost);
            electrodePotentialBuffer.CopyFromCPU(electrodePotentialHost);
            conductivityBuffer.CopyFromCPU(conductivityHost);
            neighborIndexBuffer.CopyFromCPU(neighborIndicesHost);
            neighborIsWallBuffer.CopyFromCPU(neighborIsWallHost);

            if (_initializeKernel == null)
                throw new NullReferenceException();

            _initializeKernel(elementCount,
                fiBuffer.View,
                fiNextBuffer.View,
                isWallBuffer.View,
                isElectrodeBuffer.View,
                electrodeIsSourceBuffer.View,
                electrodeCurrentBuffer.View,
                electrodePotentialBuffer.View,
                neighborIsWallBuffer.View,
                LatticeBoltzmannCudaContext.OppositeView,
                LatticeBoltzmannCudaContext.WeightsView);
            accelerator.Synchronize();

            double[] prevPhi = new double[elementCount];

            if (_collisionKernel == null || _streamKernel == null || _updateKernel == null || _phiKernel == null)
                throw new NullReferenceException();

            for (int t = 0; t < maxIter; t++)
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

                _updateKernel(elementCount,
                    fiBuffer.View,
                    fiNextBuffer.View,
                    isWallBuffer.View,
                    isElectrodeBuffer.View,
                    electrodeIsSourceBuffer.View,
                    electrodeCurrentBuffer.View,
                    electrodePotentialBuffer.View,
                    neighborIsWallBuffer.View,
                    LatticeBoltzmannCudaContext.OppositeView,
                    LatticeBoltzmannCudaContext.WeightsView);

                if (t % checkFreq == 0)
                {
                    accelerator.Synchronize();
                    _phiKernel(elementCount, fiBuffer.View, phiBuffer.View);
                    accelerator.Synchronize();
                    var phiHost = phiBuffer.GetAsArray1D();

                    double num = 0.0;
                    double den = 0.0;

                    for (int i = 0; i < phiHost.Length; i++)
                    {
                        double diff = phiHost[i] - prevPhi[i];
                        num += diff * diff;
                        den += phiHost[i] * phiHost[i];
                    }

                    if (den > 0.0 && Math.Sqrt(num / den) < tol)
                        break;
                    Array.Copy(phiHost, prevPhi, phiHost.Length);
                }
            }

            accelerator.Synchronize();
            var finalFi = fiBuffer.GetAsArray1D();

            var dict = new Dictionary<int, double>(elementCount);
            for (int idx = 0; idx < elementCount; idx++)
            {
                var element = elements[idx];
                int baseIndex = idx * 9;
                for (int k = 0; k < 9; k++)
                {
                    element.Fi[k] = finalFi[baseIndex + k];
                    element.Fi_next[k] = 0.0;
                }

                double phi = 0.0;
                for (int k = 0; k < 9; k++)
                    phi += finalFi[baseIndex + k];
                dict[elementIds[idx]] = phi;
            }

            var pd = new PotentialDistribution(dict);
            lbmGrid.SetPotentialDistribution(pd);
            return pd;
        }

        private static void EnsureCudaKernels()
        {
            LatticeBoltzmannCudaContext.EnsureInitialized();
            if (_initializeKernel != null)
                return;

            lock (_cudaKernelLock)
            {
                if (_initializeKernel != null)
                    return;

                var accelerator = LatticeBoltzmannCudaContext.Accelerator;
                _initializeKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(InitializeKernel);
                _collisionKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<int>, ArrayView<double>, double, double, ArrayView<double>>(CollisionKernel);
                _streamKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<int>>(StreamingKernel);
                _updateKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<int>, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<int>, ArrayView<double>>(UpdateKernel);
                _phiKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>>(PhiKernel);
            }
        }

        private static void InitializeKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<double> fiNext,
            ArrayView<int> isWall,
            ArrayView<int> isElectrode,
            ArrayView<int> electrodeIsSource,
            ArrayView<double> electrodeCurrent,
            ArrayView<double> electrodePotential,
            ArrayView<int> neighborIsWall,
            ArrayView<int> opposite,
            ArrayView<double> weights)
        {
            int baseIndex = index * 9;

            for (int k = 0; k < 9; k++)
            {
                fi[baseIndex + k] = weights[k];
                fiNext[baseIndex + k] = 0.0;
            }

            if (isWall[index] == 1)
                return;

            if (isElectrode[index] == 1)
            {
                if (electrodeIsSource[index] == 1)
                {
                    double current = electrodeCurrent[index];
                    for (int k = 0; k < 9; k++)
                    {
                        double value = weights[k] * current;
                        fi[baseIndex + k] = value;
                    }

                    for (int k = 0; k < 9; k++)
                    {
                        if (neighborIsWall[baseIndex + k] == 1)
                        {
                            int opp = opposite[k];
                            double value = fi[baseIndex + k];
                            fi[baseIndex + opp] += value;
                            fi[baseIndex + k] = 0.0;
                        }
                    }
                }
                else
                {
                    double potential = electrodePotential[index];
                    for (int k = 0; k < 9; k++)
                        fi[baseIndex + k] = weights[k] * potential;
                }
            }
        }

        private static void CollisionKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<int> isWall,
            ArrayView<double> conductivity,
            double csSquared,
            double minTau,
            ArrayView<double> weights)
        {
            if (isWall[index] == 1)
                return;

            int baseIndex = index * 9;
            double phi = 0.0;
            for (int k = 0; k < 9; k++)
                phi += fi[baseIndex + k];

            double tau = conductivity[index] / csSquared + 0.5;
            if (tau < minTau)
                tau = minTau;
            double omega = 1.0 / tau;

            for (int k = 0; k < 9; k++)
            {
                double geq = weights[k] * phi;
                double value = fi[baseIndex + k];
                fi[baseIndex + k] = value - omega * (value - geq);
            }
        }

        private static void StreamingKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<double> fiNext,
            ArrayView<int> isWall,
            ArrayView<int> neighborIndices,
            ArrayView<int> neighborIsWall,
            ArrayView<int> opposite)
        {
            if (isWall[index] == 1)
                return;

            int baseIndex = index * 9;
            for (int k = 0; k < 9; k++)
            {
                double value = fi[baseIndex + k];
                int neighborIndex = neighborIndices[baseIndex + k];

                if (neighborIndex >= 0 && neighborIsWall[baseIndex + k] == 0)
                {
                    ref double destination = ref fiNext[neighborIndex * 9 + k];
                    Atomic.Exchange(ref destination, value);
                }
                else if (neighborIndex >= 0)
                {
                    int opp = opposite[k];
                    ref double bounceDestination = ref fiNext[baseIndex + opp];
                    Atomic.Exchange(ref bounceDestination, value);
                }
            }
        }

        private static void UpdateKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<double> fiNext,
            ArrayView<int> isWall,
            ArrayView<int> isElectrode,
            ArrayView<int> electrodeIsSource,
            ArrayView<double> electrodeCurrent,
            ArrayView<double> electrodePotential,
            ArrayView<int> neighborIsWall,
            ArrayView<int> opposite,
            ArrayView<double> weights)
        {
            if (isWall[index] == 1)
                return;

            int baseIndex = index * 9;
            for (int k = 0; k < 9; k++)
            {
                fi[baseIndex + k] = fiNext[baseIndex + k];
                fiNext[baseIndex + k] = 0.0;
            }

            if (isElectrode[index] == 0)
                return;

            if (electrodeIsSource[index] == 1)
            {
                double current = electrodeCurrent[index];
                for (int k = 0; k < 9; k++)
                {
                    double value = weights[k] * current;
                    fi[baseIndex + k] = value;
                }

                for (int k = 0; k < 9; k++)
                {
                    if (neighborIsWall[baseIndex + k] == 1)
                    {
                        int opp = opposite[k];
                        double value = fi[baseIndex + k];
                        fi[baseIndex + opp] += value;
                        fi[baseIndex + k] = 0.0;
                    }
                }
            }
            else
            {
                double potential = electrodePotential[index];
                for (int k = 0; k < 9; k++)
                    fi[baseIndex + k] = weights[k] * potential;
            }
        }

        private static void PhiKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<double> phiOut)
        {
            int baseIndex = index * 9;
            double phi = 0.0;
            for (int k = 0; k < 9; k++)
                phi += fi[baseIndex + k];
            phiOut[index] = phi;
        }
    }
}
