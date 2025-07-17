using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// LBM-based solver for solving the diffusion PDE ∇·(γ∇φ)=f via D2Q9 lattice.
    /// Implements collision, streaming, bounce-back, and CEM boundary directly.
    /// Only uses LatticeBoltzmannOperators for inverse finite-difference gradient.
    /// </summary>
    public sealed class LatticeBoltzmannSolver
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
            4.0/9.0,
            1.0/9.0,1.0/9.0,1.0/9.0,1.0/9.0,
            1.0/36.0,1.0/36.0,1.0/36.0,1.0/36.0
        };

        /// <summary>
        /// Runs the forward LBM until steady-state, returning electrode potentials.
        /// </summary>
        public PotentialDistribution RunForward(LBMMesh mesh, LBMBoundaryCondition bc, int maxIter = 10000, double tol = 1e-6, int checkFreq = 100)
        {
            // 1) Initialize distributions Fi and Fi_next to zero
            foreach (var el in mesh.Elements)
            {
                for (int k = 0; k < 9; k++)
                {
                    el.Fi[k] = W[k] * 0.0;      // equilibrium with φ=1
                    el.Fi_next[k] = 0.0;
                }
                if(el.IsElectrode)
                {
                    var correspondingElectrode = mesh.Electrodes.Find(x => x.GridId == el.Id);

                    if (correspondingElectrode != null && bc.IsNeumann)
                        correspondingElectrode.Current = bc.Electrodes[correspondingElectrode.Id].Current;
                    else if (correspondingElectrode != null && !bc.IsNeumann)
                    {
                        if(correspondingElectrode.IsExcitation || correspondingElectrode.IsGround)
                        {
                            double current = correspondingElectrode.Current;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = W[i] * current;

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
                        else
                        {
                            correspondingElectrode.Potential = bc.Electrodes[correspondingElectrode.Id].Potential;
                            for (int i = 0; i < 9; i++)
                                el.Fi[i] = W[i] * correspondingElectrode.Potential;
                        }

                    }
                }
            }

            var elements = mesh.Elements.Cast<LBMElement>();


            // 2) Load conductivity γ into each element
            var sigmaDist = mesh.GetConductivityDistribution();
            foreach (var el in elements)
                el.Conductivity = sigmaDist.GetConductivity(el.Id);

            // 3) Mark electrodes as pinned Dirichlet
            foreach (var electrode in bc.Electrodes)
            {
                var cell = elements.First(e => e.Id == electrode.GridId);
                if (cell != null)
                    cell.IsElectrode = true;
            }

            // 4) Main loop
            double[] prevPhi = new double[mesh.Elements.Count];
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

                    // Relaxation time τ = D + 0.5, D = γ
                    double tau = el.Conductivity + 0.5;

                    if (tau <= 0.5)
                        throw new InvalidOperationException("Nonphysical tau <= 0.5");
                    double omega = 1.0 / tau;

                    // BGK collision towards equilibrium geq = W[k]*phi
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
                        var electrode = mesh.Electrodes.First(x => x.GridId == el.Id);
                        double potential = electrode.Potential;

                        // Neumann
                        if(electrode.IsExcitation || electrode.IsGround)
                        {
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

            // 5) Extract electrode potentials to return
            var dict = new Dictionary<int, double>();
            foreach (var elemenet in mesh.Elements)
                dict.Add(elemenet.Id, elemenet.Fi.Sum());
            return new PotentialDistribution(dict);
        }

        /// <summary>
        /// Inverse solve using finite-difference gradient (calls LatticeBoltzmannOperators).
        /// </summary>
        public ConductivityDistribution InverseSolve(LBMMesh mesh, LBMBoundaryCondition bc, Complex[] observed, int maxIter, double delta, double stepSize)
        {
            for (int k = 0; k < maxIter; k++)
            {
                // forward
                var Usim = RunForward(mesh, bc);

                // gradient via FD
               //var grad = LatticeBoltzmannOperators.ComputeFiniteDifferenceGradient(mesh, bc, observed, this, delta);
               //
               //// descend
               //var curr = mesh.GetConductivityDistribution();
               //var next = new Dictionary<int, double>();
               //
               //foreach (var kv in curr.Conductivities)
               //{
               //    double conductivity = kv.Value;
               //    double gradient = grad.GetConductivity(kv.Key);
               //    double nextvalue = Math.Max(1e-6, conductivity - stepSize * gradient);
               //    next[kv.Key] = nextvalue;
               //}
               //
               //mesh.SetConductivityDistribution(new ConductivityDistribution(next));
            }
            return mesh.GetConductivityDistribution();
        }
    }
}