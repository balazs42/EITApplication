using Google.OrTools.LinearSolver;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.ErrorMetrics
{
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

        // Cached arc-length ground cost for the current geometry.
        private readonly object _costCacheLock = new();
        private IDiscretization? _cachedDiscretization;
        private int[]? _cachedElectrodeIds;
        private double[,]? _cachedArcLengthCost;

        /// <summary>
        /// Standalone W₂ routine used both by the error metric and unit tests.
        /// Inputs are raw (unnormalized, possibly signed) masses and the
        /// corresponding support coordinates.  The masses are shifted to be
        /// nonnegative, normalized to unit sum, and the primal LP is solved.
        /// </summary>
        public static OTResult w2_misfit_and_grad(double[] mPred, double[] dObs,
            (double x, double y)[] x, (double x, double y)[] y)
        {
            if (x.Length != y.Length)
                throw new ArgumentException("Arc-length ground cost requires matching supports.");

            var cost = ArcLengthGroundCostHelper.BuildArcLengthCost(x);
            return w2_misfit_and_grad(mPred, dObs, cost);
        }

        public static OTResult w2_misfit_and_grad(double[] mPred, double[] dObs, double[,] costMatrix)
        {
            if (mPred.Length != costMatrix.GetLength(0) || dObs.Length != costMatrix.GetLength(1))
                throw new ArgumentException("Mass arrays must align with the ground cost matrix dimensions.");

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
            if (minA < 0)
            {
                for (int i = 0; i < a.Length; i++)
                    a[i] -= minA;
            }

            if (minB < 0)
            {
                for (int j = 0; j < b.Length; j++)
                    b[j] -= minB;
            }

            for (int i = 0; i < a.Length; i++) 
                if (a[i] < 0) 
                    a[i] = 0.0;
            for (int j = 0; j < b.Length; j++) 
                if (b[j] < 0) 
                    b[j] = 0.0;

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

            for (int i = 0; i < a.Length; i++) 
                a[i] /= sumA;
            for (int j = 0; j < b.Length; j++) 
                b[j] /= sumB;

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
            {
                for (int j = 0; j < n; j++)
                {
                    plan[i, j] = solver.MakeNumVar(0.0, double.PositiveInfinity, $"P[{i},{j}]");
                    row[i].SetCoefficient(plan[i, j], 1.0);
                    col[j].SetCoefficient(plan[i, j], 1.0);

                    obj.SetCoefficient(plan[i, j], costMatrix[i, j]);
                }
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
            
            for (int i = 0; i < m; i++) 
                phi[i] = row[i].DualValue();
            
            for (int j = 0; j < n; j++) 
                psi[j] = col[j].DualValue();

            // Gradient w.r.t normalized source histogram
            double[] grad = new double[m];
            for (int i = 0; i < m; i++) 
                grad[i] = 0.5 * phi[i];
            
            double mean = 0.0;
            for (int i = 0; i < m; i++)
                mean += grad[i] * a[i];

            for (int i = 0; i < m; i++)
                grad[i] -= mean;

            // Chain rule back to raw (unnormalized) masses
            double[] gradRaw = new double[m];
            for (int i = 0; i < m; i++)
                gradRaw[i] = grad[i] / sumA;

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
            Func<Electrode, (double x, double y)> coord;
            if (discretization is LBMGrid lbm)
                coord = e => { var le = (LBMElectrode)e; return ToXY(lbm, le.GridId); };
            else if (discretization is FEMMesh fem)
                coord = e => { var fe = (FEMElectrode)e; return GetCoord(fem, fe); };
            else
                throw new ArgumentException("Wasserstein-2 currently implemented for LBMGrid or FEMMesh because it needs electrode coordinates.");

            var all = discretization.GetElectrodes().OrderBy(e => e.Id).ToList();
            var measuring = all.Where(e => e.IsMeasuring).OrderBy(e => e.Id).ToList();
            var pattern = Workspace.GetMeasurementPattern();
            bool usingDifferences = pattern?.Representation == MeasurementRepresentation.PotentialDifference;

            if (!usingDifferences)
            {
                var differenceElectrodes = measuring.Count > 0 ? (IReadOnlyList<Electrode>)measuring : all;
                int expectedDifferenceLength = Math.Max(0, differenceElectrodes.Count - 1);
                usingDifferences = measured.Length == expectedDifferenceLength &&
                                    simulated.Length == expectedDifferenceLength;
            }

            if (usingDifferences)
                return SolveDifferenceOT2(discretization, measured, simulated, all, coord, pattern);

            if (all.Count != measured.Length || all.Count != simulated.Length)
                throw new ArgumentException("Electrode count must match data length when using direct potentials.");

            EnsureArcLengthCost(discretization, all, coord);

            // Determine which electrodes carry valid measurements.  Include
            // an electrode if the corresponding measured value is finite,
            // regardless of whether it is flagged as an excitation.  This
            // allows active-electrode LBM setups where excitation electrodes
            // also provide measurements.
            var include = new List<int>();
            for (int i = 0; i < measured.Length; i++)
                if (double.IsFinite(measured[i]))
                    include.Add(i);

            var (aRaw, aIdx, aMap) = BuildDistribution(simulated, all, include);
            var (bRaw, _, _) = BuildDistribution(measured, all, include);

            if (aRaw.Length == 0 || bRaw.Length == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[all.Count]);

            var cost = GetSubsetCost(discretization, all, coord, aIdx);
            var res = w2_misfit_and_grad(aRaw, bRaw, cost);
            var gradFull = new double[all.Count];
            foreach (var (srcIdx, electrodeIdx) in aMap)
                gradFull[electrodeIdx] = res.Grad[srcIdx];

            return new OptimalTransportResult(measured, simulated, res.Cost, gradFull);
        }

        private OptimalTransportResult SolveDifferenceOT(IDiscretization discretization, double[] measured, double[] simulated,
            IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord,
            MeasurementPattern? pattern)
        {
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated difference arrays must have identical length.");

            int differenceCount = measured.Length;
            EnsureArcLengthCost(discretization, electrodes, getCoord);
            var include = new List<int>();
            for (int i = 0; i < differenceCount; i++)
                if (double.IsFinite(measured[i]) && double.IsFinite(simulated[i]))
                    include.Add(i);

            if (include.Count == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

            var activePattern = pattern != null && pattern.SanitizedLength == differenceCount
                ? pattern
                : null;

            var (aRaw, aIdx, aMap) = BuildDifferenceDistribution(simulated, electrodes, include, activePattern);
            var (bRaw, _, _) = BuildDifferenceDistribution(measured, electrodes, include, activePattern);

            if (aRaw.Length == 0 || bRaw.Length == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

            var cost = GetSubsetCost(discretization, electrodes, getCoord, aIdx);
            var res = w2_misfit_and_grad(aRaw, bRaw, cost);

            var grad = new double[differenceCount];
            foreach (var (srcIdx, diffIdx) in aMap)
                grad[diffIdx] = res.Grad[srcIdx];

            return new OptimalTransportResult(measured, simulated, res.Cost, grad);
        }

        private OptimalTransportResult SolveDifferenceOT2(IDiscretization discretization,
                                                        double[] measured, double[] simulated,
                                                        IReadOnlyList<Electrode> electrodes,
                                                        Func<Electrode, (double x, double y)> getCoord,
                                                        MeasurementPattern? pattern)
        {
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated difference arrays must have identical length.");

            int differenceCount = measured.Length;
            EnsureArcLengthCost(discretization, electrodes, getCoord);

            // Use only channels that are finite on both sides (your existing logic).
            var include = new List<int>(differenceCount);
            for (int i = 0; i < differenceCount; i++)
                if (double.IsFinite(measured[i]) && double.IsFinite(simulated[i]))
                    include.Add(i);

            if (include.Count == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

            // Pattern provides (left,right) and we use LEFT electrode coords for channel supports.
            // (Your BuildDifferenceDistribution already does this.)
            var activePattern = pattern != null && pattern.SanitizedLength == differenceCount ? pattern : null;

            // Build raw values and channel coordinates (aligned) + back-map
            var (aRaw, aIdx, aMap) = BuildDifferenceDistribution(simulated, electrodes, include, activePattern);
            var (bRaw, _, _) = BuildDifferenceDistribution(measured, electrodes, include, activePattern);

            if (aRaw.Length == 0 || bRaw.Length == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

            // ---- Positive/Negative split on the SAME supports ----
            int m = aRaw.Length;
            double[] aPlus = new double[m];
            double[] aMinus = new double[m];
            double[] bPlus = new double[m];
            double[] bMinus = new double[m];

            for (int i = 0; i < m; i++)
            {
                double av = aRaw[i];
                double bv = bRaw[i];
                if (av > 0) aPlus[i] = av; else aMinus[i] = -av;
                if (bv > 0) bPlus[i] = bv; else bMinus[i] = -bv;
            }

            // Run two ordinary W2 solves (your existing routine) on the SAME coords.
            // NOTE: w2_misfit_and_grad internally normalizes to unit mass and returns
            // a gradient in "raw" units (scaled by 1/sum of its input), so we reweight.
            var cost = GetSubsetCost(discretization, electrodes, getCoord, aIdx);
            var resPlus = w2_misfit_and_grad(aPlus, bPlus, cost);
            var resMinus = w2_misfit_and_grad(aMinus, bMinus, cost);

            // Reweight by original masses so the two pieces contribute proportionally.
            double massPlusA = aPlus.Sum();
            double massMinusA = aMinus.Sum();

            var gradSigned = new double[m];
            for (int i = 0; i < m; i++)
                gradSigned[i] = massPlusA * resPlus.Grad[i] - massMinusA * resMinus.Grad[i];

            // Explicitly remove constant mode (robust for ring graphs: S^T * 1 = 0).
            double mean = 0.0;
            for (int i = 0; i < m; i++) mean += gradSigned[i];
            mean /= m;
            for (int i = 0; i < m; i++) gradSigned[i] -= mean;

            // Map the channel-space gradient back to the original difference vector layout.
            var gradOut = new double[differenceCount];
            foreach (var (srcIdx, diffIdx) in aMap)
                gradOut[diffIdx] = gradSigned[srcIdx];

            // (Optional) combine costs for reporting; mass-weighted average is a reasonable choice.
            double massTot = massPlusA + massMinusA + 1e-12;
            double totalCost = (massPlusA * resPlus.Cost + massMinusA * resMinus.Cost) / massTot;

            return new OptimalTransportResult(measured, simulated, totalCost, gradOut);
        }

        // Signed-split W2 for PotentialDifference with a tiny mass/balance penalty
        // and optional contrast shaping on magnitudes.
        // Place inside Wasserstein2ErrorMetric (same class as your other Solve* methods).
        private OptimalTransportResult SolveDifferencesOt2WithExtension(
            IDiscretization discretization,
            double[] measured,
            double[] simulated,
            IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord,
            MeasurementPattern? pattern,
            double lambdaMass = 1e-2,   // small amplitude anchor; try 1e-3..1e-1
            double gamma = 1.0          // optional contrast on |d|: 1.0 = off; <1 boosts small signals
        )
        {
            if (measured == null || simulated == null)
                throw new ArgumentNullException("Measured/simulated must be non-null.");
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated difference arrays must have identical length.");

            int differenceCount = measured.Length;
            EnsureArcLengthCost(discretization, electrodes, getCoord);

            // Keep only finite channels on both sides
            var include = new List<int>(differenceCount);
            for (int i = 0; i < differenceCount; i++)
                if (double.IsFinite(measured[i]) && double.IsFinite(simulated[i]))
                    include.Add(i);

            if (include.Count == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

            // Build per-channel values and coordinates (uses your existing helper).
            // IMPORTANT: ensure BuildDifferenceDistribution uses LEFT-electrode coordinates (not midpoints).
            var (aRaw, aIdx, aMap) = BuildDifferenceDistribution(simulated, electrodes, include, pattern);
            var (bRaw, _, _) = BuildDifferenceDistribution(measured, electrodes, include, pattern);

            int m = aRaw.Length;
            if (m == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

            // Optional monotone contrast shaping on magnitudes to avoid near-uniform histograms.
            // Keeps sign; apply the SAME transform to both sides.
            if (gamma != 1.0)
            {
                double Pow(double v, double g) => Math.Pow(Math.Abs(v), g) * Math.Sign(v);
                for (int i = 0; i < m; i++) { aRaw[i] = Pow(aRaw[i], gamma); bRaw[i] = Pow(bRaw[i], gamma); }
            }

            // Signed split
            var aPlus = new double[m];
            var aMinus = new double[m];
            var bPlus = new double[m];
            var bMinus = new double[m];

            for (int i = 0; i < m; i++)
            {
                double av = aRaw[i], bv = bRaw[i];
                if (av > 0) aPlus[i] = av; else aMinus[i] = -av;
                if (bv > 0) bPlus[i] = bv; else bMinus[i] = -bv;
            }

            // Masses and zero-mass guards
            const double eps = 1e-12;
            double massPlusA = 0.0, massMinusA = 0.0;
            double massPlusB = 0.0, massMinusB = 0.0;
            int cntPlus = 0, cntMinus = 0;

            for (int i = 0; i < m; i++)
            {
                massPlusA += aPlus[i]; massMinusA += aMinus[i];
                massPlusB += bPlus[i]; massMinusB += bMinus[i];
                if (aPlus[i] > 0) cntPlus++;
                if (aMinus[i] > 0) cntMinus++;
            }

            // Two standard W2 solves (use your existing routine)
            // If one side has ~zero mass on both measured+simulated, skip its call and set grad=0, cost=0.
            (double Cost, double[] Grad) resPlus, resMinus;

            var cost = GetSubsetCost(discretization, electrodes, getCoord, aIdx);

            if (massPlusA < eps && massPlusB < eps)
                resPlus = (0.0, new double[m]);
            else
            {
                var otRes = w2_misfit_and_grad(aPlus, bPlus, cost);
                resPlus = (otRes.Cost, otRes.Grad);
            }

            if (massMinusA < eps && massMinusB < eps)
                resMinus = (0.0, new double[m]);
            else
            {
                var otRes = w2_misfit_and_grad(aPlus, bPlus, cost);
                resMinus = (otRes.Cost, otRes.Grad);
            }

            // Combine gradients with mass reweighting (keeps physical scale)
            var gradSigned = new double[m];
            for (int i = 0; i < m; i++)
                gradSigned[i] = massPlusA * resPlus.Grad[i] - massMinusA * resMinus.Grad[i];

            // ---- Small mass/balance penalty (anchors amplitude; zero-mean by construction) ----
            // rPlus promotes A^+ ≈ B^+; rMinus promotes A^- ≈ B^-
            double rPlus = lambdaMass * (massPlusA - massPlusB) / Math.Max(massPlusB, eps);
            double rMinus = lambdaMass * (massMinusA - massMinusB) / Math.Max(massMinusB, eps);

            if (massPlusA > eps && cntPlus > 0)
            {
                double invA = 1.0 / massPlusA;
                double invN = 1.0 / cntPlus;
                for (int i = 0; i < m; i++)
                    if (aPlus[i] > 0)
                        gradSigned[i] += rPlus * (aPlus[i] * invA - invN);
            }

            if (massMinusA > eps && cntMinus > 0)
            {
                double invA = 1.0 / massMinusA;
                double invN = 1.0 / cntMinus;
                for (int i = 0; i < m; i++)
                    if (aMinus[i] > 0)
                        gradSigned[i] -= rMinus * (aMinus[i] * invA - invN);
            }
            // -------------------------------------------------------------------------------

            // Remove constant mode explicitly (robust for cycle graphs: S^T * 1 = 0)
            double mean = 0.0;
            for (int i = 0; i < m; i++) mean += gradSigned[i];
            mean /= m;
            for (int i = 0; i < m; i++) gradSigned[i] -= mean;

            // Map back to original difference layout
            var gradOut = new double[differenceCount];
            foreach (var (srcIdx, diffIdx) in aMap)
                gradOut[diffIdx] = gradSigned[srcIdx];

            // Costs for reporting / line-search
            double massTotA = massPlusA + massMinusA + eps;
            double costW2 = (massPlusA * resPlus.Cost + massMinusA * resMinus.Cost) / massTotA;

            double costMass = 0.5 * lambdaMass * (
                Math.Pow((massPlusA - massPlusB) / Math.Max(massPlusB, eps), 2.0) +
                Math.Pow((massMinusA - massMinusB) / Math.Max(massMinusB, eps), 2.0)
            );

            double totalCost = costW2 + costMass;

            return new OptimalTransportResult(measured, simulated, totalCost, gradOut);
        }



        private void EnsureArcLengthCost(IDiscretization discretization, IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord)
        {
            lock (_costCacheLock)
            {
                bool reuse = _cachedArcLengthCost != null && _cachedElectrodeIds != null &&
                             ReferenceEquals(_cachedDiscretization, discretization) &&
                             _cachedElectrodeIds.Length == electrodes.Count;

                if (reuse)
                {
                    for (int i = 0; i < electrodes.Count; i++)
                    {
                        if (_cachedElectrodeIds[i] != electrodes[i].Id)
                        {
                            reuse = false;
                            break;
                        }
                    }
                }

                if (reuse)
                    return;

                var coords = electrodes.Select(getCoord).ToArray();
                _cachedArcLengthCost = ArcLengthGroundCostHelper.BuildArcLengthCost(coords);
                _cachedElectrodeIds = electrodes.Select(e => e.Id).ToArray();
                _cachedDiscretization = discretization;
            }
        }

        private double[,] GetSubsetCost(IDiscretization discretization, IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord, IReadOnlyList<int> electrodeIndices)
        {
            EnsureArcLengthCost(discretization, electrodes, getCoord);
            return ArcLengthGroundCostHelper.SliceCostMatrix(_cachedArcLengthCost!, electrodeIndices);
        }

        private static (double[] raw, int[] electrodeIndices, List<(int srcIdx, int electrodeIdx)> indexMap)
            BuildDistribution(double[] raw, List<Electrode> electrodes, List<int> include)
        {
            var vals = new List<double>(include.Count);
            var map = new List<(int, int)>(include.Count);
            var indices = new List<int>(include.Count);

            foreach (int i in include)
            {
                double v = raw[i];
                if (!double.IsFinite(v))
                    continue;

                vals.Add(v);
                indices.Add(i);
                map.Add((vals.Count - 1, i));
            }
            return (vals.ToArray(), indices.ToArray(), map);
        }

        private static (double[] raw, int[] electrodeIndices, List<(int srcIdx, int diffIdx)> indexMap)
            BuildDifferenceDistribution(double[] raw,
                                        IReadOnlyList<Electrode> electrodes,
                                        List<int> include,
                                        MeasurementPattern? pattern)
        {
            var vals = new List<double>(include.Count);
            var map = new List<(int, int)>(include.Count);
            var indices = new List<int>(include.Count);

            foreach (int diffIdx in include)
            {
                if (diffIdx < 0 || diffIdx >= raw.Length)
                    continue;

                double v = raw[diffIdx];
                if (!double.IsFinite(v))
                    continue;

                int left;
                int right;
                if (pattern != null && pattern.TryGetChannel(diffIdx, out var channel))
                {
                    left = channel.FirstElectrodeIndex;
                    right = channel.SecondElectrodeIndex;
                }
                else
                {
                    left = diffIdx % electrodes.Count;
                    right = (diffIdx + 1) % electrodes.Count;
                }

                if (left < 0 || left >= electrodes.Count)
                    continue;
                if (right < 0 || right >= electrodes.Count)
                    continue;

                vals.Add(v);
                indices.Add(left);
                map.Add((vals.Count - 1, diffIdx));
            }

            return (vals.ToArray(), indices.ToArray(), map);
        }

        private static (double x, double y) ToXY(LBMGrid mesh, int gridId) =>
            LbmElectrodeCoordinateHelper.ToPhysicalCoordinates(mesh, gridId);

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
