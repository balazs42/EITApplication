using System;
using System.Collections.Generic;
using System.Linq;
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
        // D2Q9 discrete velocities
        private static readonly (int cx, int cy)[] C =
        {
            (0,0), (1,0), (0,1), (-1,0), (0,-1), (1,1), (-1,1), (-1,-1), (1,-1)
        };
        // Opposite direction lookup
        private static readonly int[] Opposite = { 0, 3, 4, 1, 2, 7, 8, 5, 6 };
        // Weights
        private static readonly double[] W =
        {
            4.0 / 9.0,
            1.0 / 9.0, 1.0 / 9.0, 1.0 / 9.0, 1.0 / 9.0,
            1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0
        };

        private static double csSquared = 1.0 / 3.0;

        private int MaxIterationCount = 250;
        private double SolutionTolerance = 1e-6;
        private int ConvergenceCheckFrequency = 100;
        private readonly bool _useCuda;

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

            var elements = lbmGrid.GetElements().Cast<LBMElement>().ToList();
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
            var bcElectrodes = bc.GetElectrodes().ToList();

            // 1) Initialize distributions Fi and Fi_next to zero
            foreach (var el in elements)
            {
                for (int k = 0; k < 9; k++)
                {
                    el.Fi[k] = W[k];        // equilibrium with φ=1
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
                                el.Fi[i] = W[i] * current;

                            // Reverse the directions which would go into walls
                            // TODO: Ground electrode should point outsidde?,
                            var neighbors = el.Neighbors;
                            for (int i = 0; i < 9; i++)
                            {
                                if (neighbors[i].IsWall)
                                {
                                    el.Fi[Opposite[i]] += el.Fi[i];
                                    el.Fi[i] = 0.0;
                                }
                            }
                        }
                        else // Prescribe the potential onn the electrodes
                        {
                            correspondingElectrode.Potential = bcElectrodes[correspondingElectrode.Id].Potential;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = W[i] * correspondingElectrode.Potential;
                        }

                    }
                }
            }

            // 2) Load conductivity γ into each element
            var sigmaDist = lbmGrid.GetConductivityDistribution();
            foreach (var el in elements)
                el.Conductivity = sigmaDist.GetConductivity(el.Id);

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
                    double tau = el.Conductivity / csSquared + 0.5;
                    if (tau <= 0.5)
                        throw new InvalidOperationException("Nonphysical tau <= 0.5");
                    double omega = 1.0 / tau;

                    // BGK collision towards equilibrium geq = W[k]*phi (thesis eq. 4.3.1)
                    for (int k = 0; k < 9; k++)
                    {
                        double geq = W[k] * phi;
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
                            el.Fi_next[Opposite[k]] = el.Fi[k];
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
                                el.Fi[i] = W[i] * current;

                            var neighbors = el.Neighbors;
                            for(int i = 0; i < 9; i++)
                            {
                                if (neighbors[i].IsWall)
                                {
                                    el.Fi[Opposite[i]] += el.Fi[i];
                                    el.Fi[i] = 0.0;
                                }
                            }
                        }
                        // Dirichlet
                        else
                        {
                            for (int k = 0; k < 9; k++)
                                el.Fi[k] = W[k] * potential;
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
            try
            {
                return RunForwardCudaInternal(lbmGrid, bc);
            }
            catch (Exception ex) when (ex is NotSupportedException or AcceleratorException)
            {
                return RunForward(lbmGrid, bc);
            }
        }

        private PotentialDistribution RunForwardCudaInternal(LBMGrid lbmGrid, LBMBoundaryCondition bc)
        {
            int maxIter = MaxIterationCount;
            double tol = SolutionTolerance;
            int checkFreq = ConvergenceCheckFrequency;

            var topology = LatticeBoltzmannGpuHelper.BuildTopology(lbmGrid);
            var elements = topology.Elements;
            int elementCount = elements.Length;

            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToArray();
            var bcElectrodes = bc.GetElectrodes().ToArray();
            var electrodeByGridId = electrodes.ToDictionary(e => e.GridId);
            var bcElectrodeById = bcElectrodes.ToDictionary(e => e.Id);

            var sigmaDist = lbmGrid.GetConductivityDistribution();
            var conductivity = new double[elementCount];
            var electrodeMode = new byte[elementCount];
            var electrodeCurrent = new double[elementCount];
            var electrodePotential = new double[elementCount];
            var fi = new double[elementCount * 9];
            var fiNext = new double[elementCount * 9];

            for (int i = 0; i < elementCount; i++)
            {
                var element = elements[i];
                conductivity[i] = sigmaDist.GetConductivity(element.Id);
                double tau = conductivity[i] / csSquared + 0.5;
                if (tau <= 0.5)
                    throw new InvalidOperationException("Nonphysical tau <= 0.5");

                byte mode = 0;
                if (electrodeByGridId.TryGetValue(element.Id, out var electrode))
                {
                    if (electrode.IsExcitation || electrode.IsGround)
                    {
                        mode = 1;
                        electrodeCurrent[i] = electrode.Current;
                    }
                    else if (bcElectrodeById.TryGetValue(electrode.Id, out var bcElectrode))
                    {
                        mode = 2;
                        electrodePotential[i] = bcElectrode.Potential;
                        electrode.Potential = bcElectrode.Potential;
                    }
                }

                electrodeMode[i] = mode;
                if (mode != 0)
                    element.IsElectrode = true;

                for (int k = 0; k < 9; k++)
                {
                    fi[i * 9 + k] = W[k];
                    fiNext[i * 9 + k] = 0.0;
                }

                if (mode == 1)
                {
                    double current = electrodeCurrent[i];
                    for (int k = 0; k < 9; k++)
                        fi[i * 9 + k] = W[k] * current;

                    for (int k = 0; k < 9; k++)
                    {
                        if (topology.NeighborExists[i * 9 + k] == 1 && topology.NeighborIsWall[i * 9 + k] == 1)
                        {
                            int opp = Opposite[k];
                            double val = fi[i * 9 + k];
                            fi[i * 9 + opp] += val;
                            fi[i * 9 + k] = 0.0;
                        }
                    }
                }
                else if (mode == 2)
                {
                    double potential = electrodePotential[i];
                    for (int k = 0; k < 9; k++)
                        fi[i * 9 + k] = W[k] * potential;
                }
            }

            foreach (var bcElectrode in bcElectrodes)
            {
                if (topology.IdToIndex.TryGetValue(bcElectrode.GridId, out var idx))
                    elements[idx].IsElectrode = true;
            }

            if (!CudaAcceleratorProvider.TryCreate(out var context, out var accelerator) || context == null || accelerator == null)
                throw new NotSupportedException("CUDA accelerator is not available.");

            using (context)
            using (accelerator)
            {
                using var fiBuffer = accelerator.Allocate1D(fi);
                using var fiNextBuffer = accelerator.Allocate1D(fiNext);
                using var conductivityBuffer = accelerator.Allocate1D(conductivity);
                using var elementIsWallBuffer = accelerator.Allocate1D(topology.ElementIsWall);
                using var neighborIndicesBuffer = accelerator.Allocate1D(topology.NeighborIndices);
                using var neighborExistsBuffer = accelerator.Allocate1D(topology.NeighborExists);
                using var neighborIsWallBuffer = accelerator.Allocate1D(topology.NeighborIsWall);
                using var electrodeModeBuffer = accelerator.Allocate1D(electrodeMode);
                using var electrodeCurrentBuffer = accelerator.Allocate1D(electrodeCurrent);
                using var electrodePotentialBuffer = accelerator.Allocate1D(electrodePotential);
                using var weightsBuffer = accelerator.Allocate1D(W);
                using var oppositeBuffer = accelerator.Allocate1D(Opposite);

                var collisionKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<byte>, double, ArrayView<double>>(CollisionKernel);
                var clearKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>>(ClearKernel);
                var streamingKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<int>, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>, ArrayView<int>>(StreamingKernel);
                var swapKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<byte>, ArrayView<byte>, ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<byte>, ArrayView<byte>, ArrayView<int>>(SwapAndBoundaryKernel);

                var fiHost = new double[elementCount * 9];
                var prevPhi = new double[elementCount];
                var phi = new double[elementCount];

                for (int t = 0; t < maxIter; t++)
                {
                    collisionKernel(elementCount, fiBuffer.View, conductivityBuffer.View, elementIsWallBuffer.View, csSquared, weightsBuffer.View);
                    clearKernel(elementCount * 9, fiNextBuffer.View);
                    streamingKernel(elementCount, fiBuffer.View, fiNextBuffer.View, neighborIndicesBuffer.View, neighborExistsBuffer.View, neighborIsWallBuffer.View, elementIsWallBuffer.View, oppositeBuffer.View);
                    swapKernel(elementCount, fiBuffer.View, fiNextBuffer.View, elementIsWallBuffer.View, electrodeModeBuffer.View, electrodeCurrentBuffer.View, electrodePotentialBuffer.View, weightsBuffer.View, neighborExistsBuffer.View, neighborIsWallBuffer.View, oppositeBuffer.View);

                    if (t % checkFreq == 0)
                    {
                        accelerator.Synchronize();
                        fiBuffer.CopyToCPU(fiHost);

                        double num = 0.0;
                        double den = 0.0;

                        for (int i = 0; i < elementCount; i++)
                        {
                            double sum = 0.0;
                            int baseIdx = i * 9;
                            for (int k = 0; k < 9; k++)
                                sum += fiHost[baseIdx + k];
                            phi[i] = sum;

                            double d = phi[i] - prevPhi[i];
                            num += d * d;
                            den += phi[i] * phi[i];
                        }

                        if (den > 0 && Math.Sqrt(num / den) < tol)
                            break;

                        Array.Copy(phi, prevPhi, elementCount);
                    }
                }

                accelerator.Synchronize();
                fiBuffer.CopyToCPU(fiHost);

                var potentialDict = new Dictionary<int, double>(elementCount);
                for (int i = 0; i < elementCount; i++)
                {
                    double sum = 0.0;
                    int baseIdx = i * 9;
                    for (int k = 0; k < 9; k++)
                    {
                        double value = fiHost[baseIdx + k];
                        elements[i].Fi[k] = value;
                        elements[i].Fi_next[k] = 0.0;
                        sum += value;
                    }
                    potentialDict[elements[i].Id] = sum;
                }

                foreach (var electrode in electrodes)
                {
                    if (topology.IdToIndex.TryGetValue(electrode.GridId, out var idx))
                        electrode.Potential = potentialDict[elements[idx].Id];
                }

                var pd = new PotentialDistribution(potentialDict);
                lbmGrid.SetPotentialDistribution(pd);
                return pd;
            }
        }

        private static void CollisionKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<double> conductivity,
            ArrayView<byte> elementIsWall,
            double csSquared,
            ArrayView<double> weights)
        {
            if (index >= conductivity.Length)
                return;
            if (elementIsWall[index] == 1)
                return;

            int baseIdx = index * 9;
            double phi = 0.0;
            for (int k = 0; k < 9; k++)
                phi += fi[baseIdx + k];

            double tau = conductivity[index] / csSquared + 0.5;
            double omega = 1.0 / tau;

            for (int k = 0; k < 9; k++)
            {
                double geq = weights[k] * phi;
                fi[baseIdx + k] += -omega * (fi[baseIdx + k] - geq);
            }
        }

        private static void ClearKernel(Index1D index, ArrayView<double> data)
        {
            if (index >= data.Length)
                return;
            data[index] = 0.0;
        }

        private static void StreamingKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<double> fiNext,
            ArrayView<int> neighborIndices,
            ArrayView<byte> neighborExists,
            ArrayView<byte> neighborIsWall,
            ArrayView<byte> elementIsWall,
            ArrayView<int> opposite)
        {
            if (index >= elementIsWall.Length)
                return;
            if (elementIsWall[index] == 1)
                return;

            int baseIdx = index * 9;
            for (int k = 0; k < 9; k++)
            {
                double value = fi[baseIdx + k];
                int neighborIndex = neighborIndices[baseIdx + k];
                byte exists = neighborExists[baseIdx + k];
                byte isWall = neighborIsWall[baseIdx + k];

                if (exists == 1 && isWall == 0)
                {
                    fiNext[neighborIndex * 9 + k] = value;
                }
                else if (exists == 1 && isWall == 1)
                {
                    int opp = opposite[k];
                    fiNext[baseIdx + opp] += value;
                }
            }
        }

        private static void SwapAndBoundaryKernel(
            Index1D index,
            ArrayView<double> fi,
            ArrayView<double> fiNext,
            ArrayView<byte> elementIsWall,
            ArrayView<byte> electrodeMode,
            ArrayView<double> electrodeCurrent,
            ArrayView<double> electrodePotential,
            ArrayView<double> weights,
            ArrayView<byte> neighborExists,
            ArrayView<byte> neighborIsWall,
            ArrayView<int> opposite)
        {
            if (index >= elementIsWall.Length)
                return;
            if (elementIsWall[index] == 1)
                return;

            int baseIdx = index * 9;
            for (int k = 0; k < 9; k++)
            {
                fi[baseIdx + k] = fiNext[baseIdx + k];
                fiNext[baseIdx + k] = 0.0;
            }

            byte mode = electrodeMode[index];
            if (mode == 1)
            {
                double current = electrodeCurrent[index];
                for (int k = 0; k < 9; k++)
                    fi[baseIdx + k] = weights[k] * current;

                for (int k = 0; k < 9; k++)
                {
                    if (neighborExists[baseIdx + k] == 1 && neighborIsWall[baseIdx + k] == 1)
                    {
                        int opp = opposite[k];
                        double val = fi[baseIdx + k];
                        fi[baseIdx + opp] += val;
                        fi[baseIdx + k] = 0.0;
                    }
                }
            }
            else if (mode == 2)
            {
                double potential = electrodePotential[index];
                for (int k = 0; k < 9; k++)
                    fi[baseIdx + k] = weights[k] * potential;
            }
        }
    }
}
