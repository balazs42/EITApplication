using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Threading.Tasks;
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
            int maxIter = MaxIterationCount;
            double tol = SolutionTolerance;
            int checkFreq = ConvergenceCheckFrequency;

            var elements = lbmGrid.GetElements().Cast<LBMElement>().ToArray();
            var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToArray();
            var bcElectrodes = bc.GetElectrodes().ToArray();

            var electrodeByGridId = electrodes.ToDictionary(e => e.GridId);
            var bcElectrodeById = bcElectrodes.ToDictionary(e => e.Id);

            Parallel.For(0, elements.Length, idx =>
            {
                var el = elements[idx];

                for (int k = 0; k < 9; k++)
                {
                    el.Fi[k] = W[k];
                    el.Fi_next[k] = 0.0;
                }

                if (el.IsElectrode && electrodeByGridId.TryGetValue(el.Id, out var electrode))
                {
                    if (electrode.IsExcitation || electrode.IsGround)
                    {
                        double current = electrode.Current;
                        for (int i = 0; i < 9; i++)
                            el.Fi[i] = W[i] * current;

                        var neighbors = el.Neighbors;
                        for (int i = 0; i < 9; i++)
                        {
                            var neighbor = neighbors[i];
                            if (neighbor != null && neighbor.IsWall)
                            {
                                el.Fi[Opposite[i]] += el.Fi[i];
                                el.Fi[i] = 0.0;
                            }
                        }
                    }
                    else if (bcElectrodeById.TryGetValue(electrode.Id, out var bcElectrode))
                    {
                        double potential = bcElectrode.Potential;
                        for (int i = 0; i < 9; i++)
                            el.Fi[i] = W[i] * potential;
                    }
                }
            });

            var sigmaDist = lbmGrid.GetConductivityDistribution();
            Parallel.For(0, elements.Length, idx =>
            {
                var el = elements[idx];
                el.Conductivity = sigmaDist.GetConductivity(el.Id);
            });

            foreach (var electrode in bcElectrodes)
            {
                var cell = elements.FirstOrDefault(e => e.Id == electrode.GridId);
                if (cell != null)
                    cell.IsElectrode = true;
            }

            double[] prevPhi = new double[elements.Length];
            for (int t = 0; t < maxIter; t++)
            {
                Parallel.For(0, elements.Length, idx =>
                {
                    var el = elements[idx];
                    if (el.IsWall)
                        return;

                    double phi = 0.0;
                    for (int k = 0; k < 9; k++)
                        phi += el.Fi[k];

                    double tau = el.Conductivity / csSquared + 0.5;
                    if (tau <= 0.5)
                        throw new InvalidOperationException("Nonphysical tau <= 0.5");
                    double omega = 1.0 / tau;

                    for (int k = 0; k < 9; k++)
                    {
                        double geq = W[k] * phi;
                        el.Fi[k] += -omega * (el.Fi[k] - geq);
                    }
                });

                Parallel.For(0, elements.Length, idx =>
                {
                    var el = elements[idx];
                    if (el.IsWall)
                        return;

                    for (int k = 0; k < 9; k++)
                    {
                        var nb = el.Neighbors[k];
                        if (nb != null && !nb.IsWall)
                        {
                            nb.Fi_next[k] = el.Fi[k];
                        }
                        else if (nb != null)
                        {
                            el.Fi_next[Opposite[k]] = el.Fi[k];
                        }
                    }
                });

                Parallel.For(0, elements.Length, idx =>
                {
                    var el = elements[idx];
                    if (el.IsWall)
                        return;

                    for (int k = 0; k < 9; k++)
                    {
                        el.Fi[k] = el.Fi_next[k];
                        el.Fi_next[k] = 0.0;
                    }

                    if (el.IsElectrode && electrodeByGridId.TryGetValue(el.Id, out var electrode))
                    {
                        if (electrode.IsExcitation || electrode.IsGround)
                        {
                            double current = electrode.Current;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = W[i] * current;

                            var neighbors = el.Neighbors;
                            for (int i = 0; i < 9; i++)
                            {
                                var neighbor = neighbors[i];
                                if (neighbor != null && neighbor.IsWall)
                                {
                                    el.Fi[Opposite[i]] += el.Fi[i];
                                    el.Fi[i] = 0.0;
                                }
                            }
                        }
                        else if (bcElectrodeById.TryGetValue(electrode.Id, out var bcElectrode))
                        {
                            double potential = bcElectrode.Potential;
                            for (int k = 0; k < 9; k++)
                                el.Fi[k] = W[k] * potential;
                        }
                    }
                });

                if (t % checkFreq == 0)
                {
                    double[] phi = new double[elements.Length];
                    for (int i = 0; i < elements.Length; i++)
                        phi[i] = elements[i].Fi.Sum();

                    double num = 0.0;
                    double den = 0.0;

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

            var dict = new Dictionary<int, double>(elements.Length);
            foreach (var element in elements)
                dict[element.Id] = element.Fi.Sum();

            var pd = new PotentialDistribution(dict);
            lbmGrid.SetPotentialDistribution(pd);
            return pd;
        }
    }
}
