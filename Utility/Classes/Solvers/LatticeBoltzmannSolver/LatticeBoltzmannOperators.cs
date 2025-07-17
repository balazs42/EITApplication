using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// Contains helper methods for applying discrete differential operators to fields
    /// defined on a structured Lattice-Boltzmann grid.
    /// This implementation is "element-centric", meaning it uses the neighbor-linking
    /// of the LBMElement class rather than coordinate-based lookups.
    /// </summary>
    public static class LatticeBoltzmannOperators
    {
        /// <summary>
        /// Central‐difference gradient of a scalar field φ on D2Q9 mesh.
        /// </summary>
        public static VectorField CalculateGradient(LBMMesh mesh, ScalarField φ)
        {
            var grad = new Dictionary<int, (double X, double Y)>();
            foreach (LBMElement el in mesh.Elements)
            {
                // cardinal neighbors
                var R = el.Neighbors[1];
                var U = el.Neighbors[2];
                var L = el.Neighbors[3];
                var D = el.Neighbors[4];

                double φ0 = φ.GetValue(el.Id);
                double φr = (R != null && !R.IsWall) ? φ.GetValue(R.Id) : φ0;
                double φl = (L != null && !L.IsWall) ? φ.GetValue(L.Id) : φ0;
                double φu = (U != null && !U.IsWall) ? φ.GetValue(U.Id) : φ0;
                double φd = (D != null && !D.IsWall) ? φ.GetValue(D.Id) : φ0;

                // h=1 → central if both exist, else one-sided
                double dx = (φr - φl) / ((R != null && L != null) ? 2.0 : 1.0);
                double dy = (φu - φd) / ((U != null && D != null) ? 2.0 : 1.0);

                grad[el.Id] = (dx, dy);
            }
            return new VectorField(grad);
        }

        /// <summary>
        /// Standard 5-point Laplacian Δφ = φ_r+φ_l+φ_u+φ_d - 4φ0.
        /// </summary>
        public static ScalarField CalculateLaplacian(LBMMesh mesh, ScalarField φ)
        {
            var lap = new Dictionary<int, double>();
            foreach (LBMElement el in mesh.Elements)
            {
                var R = el.Neighbors[1];
                var U = el.Neighbors[2];
                var L = el.Neighbors[3];
                var D = el.Neighbors[4];

                double φ0 = φ.GetValue(el.Id);
                double φr = (R != null && !R.IsWall) ? φ.GetValue(R.Id) : φ0;
                double φl = (L != null && !L.IsWall) ? φ.GetValue(L.Id) : φ0;
                double φu = (U != null && !U.IsWall) ? φ.GetValue(U.Id) : φ0;
                double φd = (D != null && !D.IsWall) ? φ.GetValue(D.Id) : φ0;

                lap[el.Id] = φr + φl + φu + φd - 4 * φ0;
            }
            var laplacian = new PotentialDistribution(lap);

            return laplacian;
        }

        /// <summary>
        /// ∇·F = ∂Fx/∂x + ∂Fy/∂y, using neighbor-based differences.
        /// </summary>
        public static ScalarField CalculateDivergence(LBMMesh mesh, VectorField F)
        {
            var div = new Dictionary<int, double>();
            foreach (LBMElement el in mesh.Elements)
            {
                var R = el.Neighbors[1];
                var U = el.Neighbors[2];
                var L = el.Neighbors[3];
                var D = el.Neighbors[4];

                var (Fx0, Fy0) = F.GetVector(el.Id);
                var (Fxr, Fyr) = (R != null) ? F.GetVector(R.Id) : (Fx0, Fy0);
                var (Fxl, Fyl) = (L != null) ? F.GetVector(L.Id) : (Fx0, Fy0);
                var (Fxu, Fyu) = (U != null) ? F.GetVector(U.Id) : (Fx0, Fy0);
                var (Fxd, Fyd) = (D != null) ? F.GetVector(D.Id) : (Fx0, Fy0);

                double dFx = (Fxr - Fxl) / ((R != null && L != null) ? 2.0 : 1.0);
                double dFy = (Fyu - Fyd) / ((U != null && D != null) ? 2.0 : 1.0);

                div[el.Id] = dFx + dFy;
            }
            return new PotentialDistribution(div);
        }

        /// <summary>
        /// Finite‐difference gradient of J wrt σ: dJ/dσ_i ≈ [J(σ+δ) - J(σ-δ)]/(2δ).
        /// </summary>
        public static ScalarField ComputeFiniteDifferenceGradient(
            LBMMesh mesh,
            LBMBoundaryCondition bc,
            Complex[] observed,
            LatticeBoltzmannSolver solver,
            double δ)
        {
            var baseσ = mesh.GetConductivityDistribution().Conductivities;
            var grad = new Dictionary<int, double>();

            // baseline
            var φ0 = solver.RunForward(mesh, bc);
            var u0 = mesh.GetElectrodePotentials();
            double J0 = 0;
            for (int e = 0; e < observed.Length; e++)
                J0 += 0.5 * Math.Pow(u0[e] - observed[e].Real, 2);

            foreach (var kv in baseσ)
            {
                int id = kv.Key;
                double σi = kv.Value;

                // σ+δ
                mesh.SetConductivity(id, σi + δ);
                solver.RunForward(mesh, bc);
                var uPlus = mesh.GetElectrodePotentials();
                double Jplus = 0;
                for (int e = 0; e < observed.Length; e++)
                    Jplus += 0.5 * Math.Pow(uPlus[e] - observed[e].Real, 2);

                // σ-δ
                mesh.SetConductivity(id, σi - δ);
                solver.RunForward(mesh, bc);
                var uMinus = mesh.GetElectrodePotentials();
                double Jminus = 0;
                for (int e = 0; e < observed.Length; e++)
                    Jminus += 0.5 * Math.Pow(uMinus[e] - observed[e].Real, 2);

                // restore
                mesh.SetConductivity(id, σi);

                grad[id] = (Jplus - Jminus) / (2 * δ);
            }

            return new ConductivityDistribution(grad);
        }
    }
}