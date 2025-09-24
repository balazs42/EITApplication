using Google.OrTools.ConstraintSolver;
using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

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

        public LatticeBoltzmannSolver(int maxIterationCount, double solutionTolerance, int convergenceCheckFrequency)
        {
            MaxIterationCount = maxIterationCount;
            SolutionTolerance = solutionTolerance;
            ConvergenceCheckFrequency = convergenceCheckFrequency;
        }

        public PotentialDistribution SolveForward(IDiscretization discretization, BoundaryCondition boundaryCondition)
        {
            var lbmGrid = discretization as LBMGrid ?? throw new InvalidCastException();
            var bc = boundaryCondition as LBMBoundaryCondition ?? throw new InvalidCastException();

            return RunForward(lbmGrid, bc);
        }

        public PotentialDistribution SolveAdjoint(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[] adjointSource)
        {
            var lbmGrid = discretization as LBMGrid ?? throw new InvalidCastException();
            var bc = boundaryCondition as LBMBoundaryCondition ?? throw new InvalidCastException();
            var electrodes = lbmGrid.GetElectrodes();
            var bcElectrodes = bc.GetElectrodes();
            int bcElectrodeCount = bcElectrodes.Count();

            for(int i = 0; i < bcElectrodeCount; i++)
            {
                bcElectrodes[i].Potential = 0.0;
                electrodes[i].Potential = 0.0;

                bcElectrodes[i].Current = adjointSource[i].Real;   // TODO: add complex currents
                electrodes[i].Current = adjointSource[i].Real;
            }

            return RunForward(lbmGrid, bc);
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
    }
}