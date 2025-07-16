using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// LBM-based solver for diffusion-like EIT forward and adjoint problems
    /// Collision+Streaming implement ∇·(γ∇φ)=f on a D2Q9 lattice.
    /// </summary>
    public sealed class LatticeBoltzmannSolver
    {
        // D2Q9 velocities
        private readonly (int cx, int cy)[] _c =
        {
            (0,0),(1,0),(0,1),
            (-1,0),(0,-1),(1,1),
            (-1,1),(-1,-1),(1,-1)
        };
        private readonly int[] _opp = { 0, 3, 4, 1, 2, 7, 8, 5, 6 };
        private readonly double[] _w = { 4.0 / 9.0,               
                                         1.0 / 9.0,  1.0 / 9.0,  1.0 / 9.0,  1.0 / 9.0, 
                                         1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0 };

        /// <summary>
        /// Runs LBM until steady state or max iterations.
        /// ConductivityDistribution now pulled from mesh, CEM boundaries use pinned potentials.
        /// </summary>
        public PotentialDistribution RunSimulation(LBMMesh mesh, LBMBoundaryCondition bc, int maxIterations, double convergenceThreshold, int checkInterval, Complex[]? source = null)
        {
            // 1) Initialize conductivities and distributions
            var condDist = mesh.GetConductivityDistribution();                      // element.Id -> γ
            Initialize(mesh, condDist);                                           // sets element.Conductivity and Fi

            // 2) Apply CEM boundary via electrode potentials in bc
            ApplyBoundaryConditions(mesh, bc);

            // 3) Build source field if adjoint
            var srcField = BuildSourceField(mesh, source);

            // 4) Time-stepping until convergence
            double[] phiPrev = new double[mesh.Elements.Count];
            for (int t = 1; t <= maxIterations; t++)
            {
                Step(mesh, srcField);

                if (t % checkInterval == 0)
                {
                    var phiNew = mesh.Elements.Cast<LBMElement>()
                                     .Select(el => el.Fi.Sum()).ToArray();
                    double num = 0, den = 0;
                    for (int i = 0; i < phiNew.Length; i++)
                    {
                        double d = phiNew[i] - phiPrev[i]; num += d * d; den += phiNew[i] * phiNew[i];
                    }
                    if (den > 1e-20 && Math.Sqrt(num / den) < convergenceThreshold) break;
                    Array.Copy(phiNew, phiPrev, phiNew.Length);
                }
            }

            // 5) Pack final potentials
            return PackResult(mesh);
        }

        /// <summary>
        /// Sets each LBM element's conductivity and resets Fi.
        /// Eq: D = γ (diffusion) → tau = D + 0.5
        /// </summary>
        private void Initialize(LBMMesh mesh, ConductivityDistribution sigma)
        {
            foreach (var el in mesh.Elements.Cast<LBMElement>())
            {
                el.Conductivity = sigma.GetConductivity(el.Id);
                el.IsElectrode = false;
                //el.IsPinned = false;

                for (int k = 0; k < 9; k++)
                    el.Fi[k] = _w[k] * 1.0;
            }
        }

        /// <summary>
        /// Tags electrode cells and pins them to given potential Uℓ (CEM Dirichlet part).
        /// </summary>
        private void ApplyBoundaryConditions(LBMMesh mesh, LBMBoundaryCondition bc)
        {
            var lookup = mesh.Elements.Cast<LBMElement>()
                             .ToDictionary(e => e.Id);
            foreach (var el in bc.Electrodes)
            {
                if (!lookup.TryGetValue(el.GridId, out var cell)) continue;
                cell.IsElectrode = true;
                //cell.IsPinned = true;
                //cell.PinValue = el.Potential;    // enforce φ=Uℓ on electrode
            }
        }

        /// <summary>
        /// Converts Complex[] source (indexed by electrode) into element-wise forcing f_i.
        /// Implements adjoint source f = S^T(s_obs - s_sim) on electrode cells.
        /// </summary>
        private Dictionary<int, double>? BuildSourceField(LBMMesh mesh, Complex[]? source)
        {
            if (source == null) return null;
            var dict = new Dictionary<int, double>();
            var els = mesh.Electrodes;
            for (int e = 0; e < els.Count && e < source.Length; e++)
            {
                dict[els[e].Id] = source[e].Real;
            }
            return dict;
        }

        /// <summary>
        /// One LBM time step: collide, stream, bounce-back, enforce pins.
        /// Collision: Fi += -ω(Fi-geq)+w[k]*f_i
        /// Streaming: push Fi to Fi_next of neighbors or bounce back
        /// </summary>
        private void Step(LBMMesh mesh, Dictionary<int, double> src)
        {
            // Collision
            foreach (var cell in mesh.Elements.Cast<LBMElement>())
            {
                if (cell.IsWall) continue;
                double phi = cell.Fi.Sum();
                double tau = cell.Conductivity + 0.5;          // Eq. τ = D + 0.5, D=γ
                double omega = 1.0 / tau;
                double fsrc = (src != null && src.TryGetValue(cell.Id, out var v)) ? v : 0.0;

                for (int k = 0; k < 9; k++)
                {
                    double geq = _w[k] * phi;                    // equilibrium for zero velocity
                    cell.Fi[k] += -omega * (cell.Fi[k] - geq) + _w[k] * fsrc;
                }
            }

            // Streaming
            foreach (var cell in mesh.Elements.Cast<LBMElement>())
            {
                if (cell.IsWall) continue;
                for (int k = 0; k < 9; k++)
                {
                    var nb = cell.Neighbors[k];
                    if (nb != null && !nb.IsWall)
                        nb.Fi_next[k] = cell.Fi[k];
                    else
                        cell.Fi_next[_opp[k]] = cell.Fi[k];
                }
            }

            // Update + enforce pin Dirichlet
            foreach (var cell in mesh.Elements.Cast<LBMElement>())
            {
                if (cell.IsWall) continue;
                Array.Copy(cell.Fi_next, cell.Fi, 9);
                Array.Clear(cell.Fi_next, 0, 9);
                //if (cell.IsPinned)
                //    for (int k = 0; k < 9; k++)
                //      cell.Fi[k] = _w[k] * cell.PinValue;
            }
        }

        /// <summary>
        /// After convergence, extracts phi at each element center and updates electrode potentials.
        /// </summary>
        private PotentialDistribution PackResult(LBMMesh mesh)
        {
            // Create a dictionary of the element ids and the \sum fi = \phi potentials
            var phis = mesh.Elements.Cast<LBMElement>().ToDictionary(el => el.Id, el => el.Fi.Sum());

            // Update electrode potentials, possibly unneccessray since the SetPotentialDistribution() call sets these values, must be checked!
            foreach (var el in mesh.Electrodes)
                el.Potential = phis[el.GridId];

            var newPotentialDistribution = new PotentialDistribution(phis);

            mesh.SetPotentialDistribution(newPotentialDistribution);

            return newPotentialDistribution;
        }
    }
}