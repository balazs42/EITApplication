using Google.OrTools.LinearSolver;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Discretizer.FiniteElementMesh;

namespace Utility.Classes.ReconstructionParameters
{
    public enum ErrorMetric
    {
        L2 = 1,
        Wasserstein2 = 2
    }

    /// <summary>
    /// Defines a misfit functional that measures the discrepancy between
    /// measured and simulated data.
    /// </summary>
    public interface IErrorMetric
    {
        /// <summary>
        /// Evaluates the misfit functional, J_misfit.
        /// This corresponds to the first term in your total cost function.
        /// </summary>
        /// <param name="measured">Observed boundary potentials.</param>
        /// <param name="simulated">Simulated boundary potentials from the forward model.</param>
        /// <returns>A scalar value representing the misfit.</returns>
        double Evaluate(IDiscretization discretization, double[] measured, double[] simulated);

        /// <summary>
        /// Evaluates the source term for the adjoint PDE problem.
        /// For L2, this is the residual (simulated - measured).
        /// For W2, this is the Kantorovich potential, φ.
        /// </summary>
        /// <param name="measured">Observed boundary potentials.</param>
        /// <param name="simulated">Simulated boundary potentials from the forward model.</param>
        /// <returns>A vector to be used as the source on the right-hand-side of the adjoint PDE.</returns>
        double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated);
    }

    /// <summary>
    /// Implements the standard L2-norm squared misfit. J = 1/2 * ||d_sim - d_obs||^2.
    /// </summary>
    public sealed class L2ErrorMetric : IErrorMetric
    {
        public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated vectors must have the same length.");

            double sumOfSquares = 0.0;
            for (int i = 0; i < measured.Length; i++)
            {
                // If either value is NaN, this point doesn't contribute to the error.
                if (double.IsNaN(measured[i]) || double.IsNaN(simulated[i])) continue;
                
                double residual = simulated[i] - measured[i];
                sumOfSquares += residual * residual;
            }
            return 0.5 * sumOfSquares;
        }

        public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated vectors must have the same length.");

            double[] residual = new double[measured.Length];
            for (int i = 0; i < measured.Length; i++)
            {
                // If a value is NaN, the residual (the source for the adjoint) should be zero.
                if (double.IsNaN(measured[i]) || double.IsNaN(simulated[i]))
                    residual[i] = 0.0;
                // adjoint PDE is ∇·(γ∇μ) = - S^T (Sϕ - d_obs),
                // so the boundary‐current we feed into our forward‐solver adjoint is
                //    Iℓ = - (ϕℓ - d_obs,ℓ) = d_obs,ℓ – ϕℓ
                else
                    residual[i] = measured[i] - simulated[i];
            }
            return residual;
        }
    }

    /// <summary>
    /// W₂²(μ,ν) via the Kantorovich dual:
    ///   maximize  ⟨u, a⟩ + ⟨v, b⟩   subject to  u_i + v_j ≤ c_{ij}
    /// where a,b are probability masses over simulated/measured electrodes (NaNs & non-measuring filtered),
    /// and c_{ij} = ||x_i - y_j||² built from LBMGrid lattice coordinates.
    /// The adjoint source equals the (discretized) Kantorovich potential φ ≡ u, mapped back to all electrodes.
    /// </summary>
    public sealed class Wasserstein2ErrorMetric : IErrorMetric
    {
        // Cache last result to reuse φ when EvaluateAdjointSource() called after Evaluate()
        private OptimalTransportResult? _last;

        public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
        {
            var ot = SolveOT(discretization, measured, simulated);
            _last = ot;
            return ot.OptimalValue;
        }

        public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (_last != null && _last.MatchesInputs(measured, simulated))
                return _last.Phi;

            // If called independently, solve once to obtain φ then return it
            return SolveOT(discretization, measured, simulated).Phi;
        }

        private OptimalTransportResult SolveOT(IDiscretization discretization, double[] measured, double[] simulated)
        {
            // (1) Gather electrodes and coordinate provider
            var all = discretization.GetElectrodes().OrderBy(e => e.Id).ToList();
            if (all.Count != measured.Length || all.Count != simulated.Length)
                throw new ArgumentException("Electrode count must match data length.");

            Func<Electrode, (double x, double y)> coord;
            if (discretization is LBMGrid lbm)
                coord = e =>
                {
                    var le = (LBMElectrode)e;
                    var (x, y) = ToXY(lbm, le.GridId);
                    return (x, y);
                };
            else if (discretization is FEMMesh fem)
                coord = e =>
                {
                    var fe = (FEMElectrode)e;
                    return GetCoord(fem, fe);
                };
            else
                throw new ArgumentException("Wasserstein-2 currently implemented for LBMGrid or FEMMesh because it needs electrode coordinates.");

            var (a, aLoc, aIndexMap, aNorm) = BuildDistribution(simulated, all, coord); // source: simulated
            var (b, bLoc, _, _) = BuildDistribution(measured, all, coord); // target: measured

            // If nothing valid (e.g., all NaN), return zero
            if (a.Length == 0 || b.Length == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[all.Count]);

            // (2) Cost matrix c_{ij} = squared Euclidean distance on coordinates
            int m = a.Length, n = b.Length;
            double[,] C = new double[m, n];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double dx = aLoc[i].x - bLoc[j].x;
                    double dy = aLoc[i].y - bLoc[j].y;
                    C[i, j] = dx * dx + dy * dy;
                }
            }

            // (3) Dual LP: maximize <u,a> + <v,b>  s.t. u_i + v_j ≤ C_ij
            var solver = Solver.CreateSolver("GLOP"); // linear programming (continuous)
            if (solver is null)
                throw new InvalidOperationException("OR-Tools LP solver 'GLOP' not available.");

            var u = new Variable[m];
            var v = new Variable[n];

            for (int i = 0; i < m; i++) u[i] = solver.MakeNumVar(double.NegativeInfinity, double.PositiveInfinity, $"u[{i}]");
            for (int j = 0; j < n; j++) v[j] = solver.MakeNumVar(double.NegativeInfinity, double.PositiveInfinity, $"v[{j}]");

            // Constraints u_i + v_j ≤ C_ij
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                {
                    var c = solver.MakeConstraint(double.NegativeInfinity, C[i, j], $"c[{i},{j}]");
                    c.SetCoefficient(u[i], 1.0);
                    c.SetCoefficient(v[j], 1.0);
                }

            // Objective: maximize sum_i u_i a_i + sum_j v_j b_j
            var obj = solver.Objective();
            for (int i = 0; i < m; i++)
                obj.SetCoefficient(u[i], a[i]);
            for (int j = 0; j < n; j++) 
                obj.SetCoefficient(v[j], b[j]);

            obj.SetMaximization();

            var status = solver.Solve();
            if (status != Solver.ResultStatus.OPTIMAL)
                throw new InvalidOperationException($"W₂ dual LP not optimal. Status={status}");

            double optimal = obj.Value();

            // Kantorovich potential φ ≡ u on the (simulated) support. Account for normalization a = raw / aNorm
            var phiFull = new double[all.Count]; // default 0 where simulated was NaN or not measuring
            if (aNorm > 0.0)
            {
                double phiDotA = 0.0;
                for (int i = 0; i < m; i++) phiDotA += u[i].SolutionValue() * a[i];
                foreach (var (iSrc, iElectrode) in aIndexMap)
                    phiFull[iElectrode] = (u[iSrc].SolutionValue() - phiDotA) / aNorm;
            }
            
            return new OptimalTransportResult(measured, simulated, optimal, phiFull);
        }

        private static (double[] mass, List<(double x, double y)> loc, List<(int srcIdx, int electrodeIdx)> indexMap, double norm)
            BuildDistribution(double[] raw, List<Electrode> electrodes, Func<Electrode, (double x, double y)> getCoord)
        {
            var vals = new List<double>();
            var coords = new List<(double x, double y)>();
            var map = new List<(int, int)>();
            for (int i = 0; i < raw.Length; i++)
            {
                var e = electrodes[i];
                if (!e.IsMeasuring) continue;
                double v = raw[i];
                if (double.IsNaN(v)) continue;
                if (v < 0.0) v = 0.0; // clamp to nonnegative
                vals.Add(v);
                coords.Add(getCoord(e));
                map.Add((vals.Count - 1, i)); // (index in 'vals', electrode index)
            }

            if (vals.Count == 0)
                return (Array.Empty<double>(), new List<(double, double)>(), new List<(int, int)>(), 0.0);

            double sum = vals.Sum();
            if (sum <= 0.0)
            {
                // fallback: uniform over valid measuring electrodes; no gradient w.r.t raw
                double p = 1.0 / vals.Count;
                for (int k = 0; k < vals.Count; k++) vals[k] = p;
                return (vals.ToArray(), coords, map, 0.0);
            }

            for (int k = 0; k < vals.Count; k++) vals[k] /= sum;

            return (vals.ToArray(), coords, map, sum);
        }

        private static (double x, double y) ToXY(LBMGrid mesh, int gridId)
        {
            // Prefer mesh API if public; otherwise decode (id = y*Nx + x)
            int x = gridId % mesh.Nx;
            int y = gridId / mesh.Nx;
            return (x, y);
        }

        private static (double x, double y) GetCoord(FEMMesh mesh, FEMElectrode e)
        {
            if (!e.PointElectrode && e.FEMVertexIds != null && e.FEMVertexIds.Count > 0)
            {
                var verts = mesh.Vertices.Where(v => e.FEMVertexIds.Contains(v.GlobalId)).ToList();
                double x = verts.Average(v => v.X);
                double y = verts.Average(v => v.Y);
                return (x, y);
            }

            var vtx = mesh.Vertices.First(v => v.GlobalId == e.MeshId);
            return (vtx.X, vtx.Y);
        }

        private sealed class OptimalTransportResult
        {
            private readonly double[] _m;
            private readonly double[] _s;
            public double OptimalValue { get; }
            public double[] Phi { get; }

            public OptimalTransportResult(double[] measured, double[] simulated, double value, double[] phi)
            {
                _m = measured; _s = simulated;
                OptimalValue = value; Phi = phi;
            }
            public bool MatchesInputs(double[] measured, double[] simulated) => ReferenceEquals(_m, measured) && ReferenceEquals(_s, simulated);
        }
    }
}