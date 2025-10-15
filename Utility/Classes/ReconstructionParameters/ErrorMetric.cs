using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.LinearSolver;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Discretizer.FiniteElementMesh;

namespace Utility.Classes.ReconstructionParameters
{
    public enum ErrorMetric
    {
        L2 = 1,
        Wasserstein2 = 2,
        ConductivityAwareW2 = 3,
        EnergyBasedWasserstein2 = 4
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
                if (double.IsNaN(measured[i]) || double.IsNaN(simulated[i])) 
                    continue;
                
                double residual = measured[i] - simulated[i];
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
    /// Implements the Wasserstein-2 misfit using the discrete optimal transport
    /// problem on electrode measurements.  We solve the primal LP
    ///   min_P  Σ₍ᵢⱼ₎ Pᵢⱼ Cᵢⱼ
    /// subject to row/column sums matching the normalized source and target
    /// histograms.  Dual variables (φ,ψ) are automatically recovered from the LP
    /// constraints.  The gradient of ½W₂² with respect to the source histogram is
    /// gₘ = ½φ, shifted to have zero mean so that Σ mᵢ gₘᵢ = 0.  Because the
    /// histograms are normalized, adding a constant to the raw data does not
    /// affect the adjoint chain.
    /// </summary>
    public sealed class Wasserstein2ErrorMetric : IErrorMetric
    {
        private const double Tiny = 1e-12;

        // Cache last result to reuse gradient when EvaluateAdjointSource() follows Evaluate().
        private OptimalTransportResult? _last;

        /// <summary>
        /// Standalone W₂ routine used both by the error metric and unit tests.
        /// Inputs are raw (unnormalized, possibly signed) masses and the
        /// corresponding support coordinates.  The masses are shifted to be
        /// nonnegative, normalized to unit sum, and the primal LP is solved.
        /// </summary>
        public static OTResult w2_misfit_and_grad(double[] mPred, double[] dObs,
            (double x, double y)[] x, (double x, double y)[] y)
        {
            if (mPred.Length != x.Length || dObs.Length != y.Length)
                throw new ArgumentException("Mass and coordinate arrays must align.");

            // Stable nonnegativity: shift by minimum and clamp.
            double[] a = (double[])mPred.Clone();
            double[] b = (double[])dObs.Clone();

            for (int i = 0; i < a.Length; i++)
                if (!double.IsFinite(a[i]))
                    a[i] = 0.0;
            for (int j = 0; j < b.Length; j++)
                if (!double.IsFinite(b[j]))
                    b[j] = 0.0;

            double minA = a.Length > 0 ? a.Min() : 0.0;
            double minB = b.Length > 0 ? b.Min() : 0.0;
            if (minA < 0) for (int i = 0; i < a.Length; i++) a[i] -= minA;
            if (minB < 0) for (int j = 0; j < b.Length; j++) b[j] -= minB;
            for (int i = 0; i < a.Length; i++) if (a[i] < 0) a[i] = 0.0;
            for (int j = 0; j < b.Length; j++) if (b[j] < 0) b[j] = 0.0;

            double sumA = a.Sum();
            double sumB = b.Sum();
            if (sumA <= Tiny || sumB <= Tiny)
            {
                // Degenerate case: all masses are identical (or arrays are empty),
                // so after shifting the total mass collapses to ~0.  In this
                // situation the Wasserstein distance is zero and the gradient
                // should vanish.  Returning a zero-cost result avoids propagating
                // an exception to callers which is observed in LBM based
                // reconstructions where electrodes may carry uniform potentials.
                int me = a.Length, ne = b.Length;
                return new OTResult(0.0,
                    new double[me],
                    new double[me, ne],
                    new double[me],
                    new double[ne]);
            }

            for (int i = 0; i < a.Length; i++) a[i] /= sumA;
            for (int j = 0; j < b.Length; j++) b[j] /= sumB;

            int m = a.Length, n = b.Length;
            var solver = Solver.CreateSolver("GLOP") ?? throw new InvalidOperationException("OR-Tools LP solver 'GLOP' not available.");

            var plan = new Variable[m, n];
            var row = new Constraint[m];
            var col = new Constraint[n];
            for (int i = 0; i < m; i++)
                row[i] = solver.MakeConstraint(a[i], a[i], $"row[{i}]");
            for (int j = 0; j < n; j++)
                col[j] = solver.MakeConstraint(b[j], b[j], $"col[{j}]");

            var obj = solver.Objective();
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                {
                    plan[i, j] = solver.MakeNumVar(0.0, double.PositiveInfinity, $"P[{i},{j}]");
                    row[i].SetCoefficient(plan[i, j], 1.0);
                    col[j].SetCoefficient(plan[i, j], 1.0);
                    double dx = x[i].x - y[j].x;
                    double dy = x[i].y - y[j].y;
                    double cij = dx * dx + dy * dy;
                    obj.SetCoefficient(plan[i, j], cij);
                }

            obj.SetMinimization();
            var status = solver.Solve();
            if (status != Solver.ResultStatus.OPTIMAL)
                throw new InvalidOperationException($"W₂ primal LP not optimal. Status={status}");

            double[,] P = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    P[i, j] = plan[i, j].SolutionValue();

            double cost = 0.5 * obj.Value();

            // Dual potentials from row/column constraints
            double[] phi = new double[m];
            double[] psi = new double[n];
            for (int i = 0; i < m; i++) phi[i] = row[i].DualValue();
            for (int j = 0; j < n; j++) psi[j] = col[j].DualValue();

            // Gradient w.r.t normalized source histogram
            double[] grad = new double[m];
            for (int i = 0; i < m; i++) grad[i] = 0.5 * phi[i];
            double mean = 0.0;
            for (int i = 0; i < m; i++) mean += grad[i] * a[i];
            for (int i = 0; i < m; i++) grad[i] -= mean;

            // Chain rule back to raw (unnormalized) masses
            double[] gradRaw = new double[m];
            for (int i = 0; i < m; i++) gradRaw[i] = grad[i] / sumA;

            return new OTResult(cost, gradRaw, P, phi, psi);
        }

        public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
        {
            var ot = SolveOT(discretization, measured, simulated);
            _last = ot;
            return ot.Cost;
        }

        public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (_last != null && _last.MatchesInputs(measured, simulated))
                return _last.Grad;
            return SolveOT(discretization, measured, simulated).Grad;
        }

        private OptimalTransportResult SolveOT(IDiscretization discretization, double[] measured, double[] simulated)
        {
            var all = discretization.GetElectrodes().OrderBy(e => e.Id).ToList();
            if (all.Count != measured.Length || all.Count != simulated.Length)
                throw new ArgumentException("Electrode count must match data length.");

            Func<Electrode, (double x, double y)> coord;
            if (discretization is LBMGrid lbm)
                coord = e => { var le = (LBMElectrode)e; return ToXY(lbm, le.GridId); };
            else if (discretization is FEMMesh fem)
                coord = e => { var fe = (FEMElectrode)e; return GetCoord(fem, fe); };
            else
                throw new ArgumentException("Wasserstein-2 currently implemented for LBMGrid or FEMMesh because it needs electrode coordinates.");

            // Determine which electrodes carry valid measurements.  Include
            // an electrode if the corresponding measured value is finite,
            // regardless of whether it is flagged as an excitation.  This
            // allows active-electrode LBM setups where excitation electrodes
            // also provide measurements.
            var include = new List<int>();
            for (int i = 0; i < measured.Length; i++)
                if (double.IsFinite(measured[i]))
                    include.Add(i);

            var (aRaw, aLoc, aMap) = BuildDistribution(simulated, all, coord, include);
            var (bRaw, bLoc, _) = BuildDistribution(measured, all, coord, include);

            if (aRaw.Length == 0 || bRaw.Length == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[all.Count]);

            var res = w2_misfit_and_grad(aRaw, bRaw, aLoc, bLoc);
            var gradFull = new double[all.Count];
            foreach (var (srcIdx, electrodeIdx) in aMap)
                gradFull[electrodeIdx] = res.Grad[srcIdx];

            return new OptimalTransportResult(measured, simulated, res.Cost, gradFull);
        }

        private static (double[] raw, (double x, double y)[] loc, List<(int srcIdx, int electrodeIdx)> indexMap)
            BuildDistribution(double[] raw, List<Electrode> electrodes,
                Func<Electrode, (double x, double y)> getCoord, List<int> include)
        {
            var vals = new List<double>(include.Count);
            var coords = new List<(double, double)>(include.Count);
            var map = new List<(int, int)>(include.Count);

            foreach (int i in include)
            {
                double v = raw[i];
                if (!double.IsFinite(v))
                    continue;

                var e = electrodes[i];
                vals.Add(v);
                coords.Add(getCoord(e));
                map.Add((vals.Count - 1, i));
            }
            return (vals.ToArray(), coords.ToArray(), map);
        }

        private static (double x, double y) ToXY(LBMGrid mesh, int gridId)
        {
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

        // Lightweight cache wrapper for Evaluate/EvaluateAdjointSource
        private sealed class OptimalTransportResult
        {
            private readonly double[] _m;
            private readonly double[] _s;
            public double Cost { get; }
            public double[] Grad { get; }

            public OptimalTransportResult(double[] measured, double[] simulated, double cost, double[] grad)
            {
                _m = measured; _s = simulated;
                Cost = cost; Grad = grad;
            }
            public bool MatchesInputs(double[] measured, double[] simulated) => ReferenceEquals(_m, measured) && ReferenceEquals(_s, simulated);
        }

        /// <summary>Result record returned by w2_misfit_and_grad.</summary>
        public sealed class OTResult
        {
            public double Cost { get; }
            public double[] Grad { get; }
            public double[,] Plan { get; }
            public double[] Phi { get; }
            public double[] Psi { get; }
            public OTResult(double cost, double[] grad, double[,] plan, double[] phi, double[] psi)
            {
                Cost = cost; Grad = grad; Plan = plan; Phi = phi; Psi = psi;
            }
        }
    }
}