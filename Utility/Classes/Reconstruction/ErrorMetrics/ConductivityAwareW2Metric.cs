using System.Buffers;
using System.Linq;
using System.Threading.Tasks;
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
    /// Conductivity-aware Wasserstein-2 error metric following the derivations:
    ///
    /// J_alpha(sigma) = 1/2 * [ (1 - alpha) W2^2_geo(mu(sigma), nu)
    ///                        + alpha       W2^2_phys(mu(sigma), nu) ].
    ///
    /// - W2_geo : geometric cost c_geo(i,j) = |x_i - x_j|^2
    /// - W2_phys: conductivity-aware cost C_sigma(i,j) ~ (shortest conductive path)^2 (scaled)
    ///
    /// The class:
    /// - Normalizes raw electrode data to probability histograms as in (4.1.3)
    /// - Solves two OT problems (geometric + conductivity-aware) when alpha > 0
    /// - Returns a convex combination of costs and gradients w.r.t. the simulated data.
    ///
    /// The gradient is with respect to the *raw* simulated data (amplitudes or differences),
    /// suitable as a right-hand side for the adjoint PDE after applying the measurement operator.
    /// </summary>
    public sealed class ConductivityAwareW2Metric : IErrorMetric
    {
        /// <summary>
        /// Public configuration knobs. Defaults keep external behavior unchanged while
        /// implementing the derivation-based J_alpha.
        /// </summary>
        public sealed class Config
        {
            /// <summary>
            /// Target alpha in [0,1] for the convex combination:
            /// J_alpha = (1-alpha)*J_geo + alpha*J_phys.
            /// </summary>
            public double TargetAlpha { get; init; } = 1.0;

            /// <summary>
            /// How many OT solves elapse between conductivity-aware ground-cost rebuilds.
            /// </summary>
            public int RecomputeEvery { get; init; } = 1;

            /// <summary>
            /// L1 change in sigma that triggers an early rebuild of the conductivity-aware ground cost.
            /// </summary>
            public double SigmaChangeTolerance { get; init; } = 1e-3;

            /// <summary>
            /// Warm-up: number of OT solves with purely geometric W2 before switching on the
            /// conductivity-aware term. If negative, defaults to measurement length.
            /// </summary>
            public int WarmupSolves { get; init; } = -1;

            /// <summary>
            /// If true we gradually ramp alpha towards TargetAlpha when rebuilding the cost.
            /// </summary>
            public bool EnableAlphaRamp { get; init; } = true;

            /// <summary>
            /// Exponent beta in the conductivity-weighted geodesic edge cost:
            /// edge_cost ~ length * sigma_edge^{-beta}. Default beta = 1.
            /// </summary>
            public double GeodesicBeta { get; init; } = 1.0;
        }

        private const double Tiny = 1e-12;
        private const double SigmaEps = 1e-12;
        private const double TinyDistance = 1e-9;

        private readonly Config _config;

        private bool _warmupInitialized;
        private int _warmupSolvesRemaining;
        private bool _useConductivityAware;
        private int _solveCounter;
        private int _lastCostBuildIter = -1;
        private double _alphaCurrent;
        private double _scale = 1.0;
        private int _maxElectrodes;

        /// <summary>
        /// Geometric ground cost matrix: c_geo(i,j) = |x_i - x_j|^2 for all electrodes on the
        /// current discretization (global electrode index space).
        /// </summary>
        private double[,]? _euclideanCost;

        /// <summary>
        /// Conductivity-aware ground cost matrix C_sigma(i,j) ~ scaled (conductive geodesic)^2.
        /// Used for both amplitude and difference data (sub-selected as needed).
        /// </summary>
        private double[,]? _cPhysAmp;
        private double[,]? _cPhysDiff;

        /// <summary>
        /// Snapshot of element conductivities used the last time we built C_sigma.
        /// </summary>
        private double[]? _sigmaSnapshot;

        /// <summary>
        /// Scratch sub-matrices for amplitude and difference sub-problems (avoid reallocations).
        /// Keys are the sub-size (number of included bins).
        /// </summary>
        private readonly Dictionary<int, double[,]> _amplitudeCostScratch = new();
        private readonly Dictionary<int, double[,]> _differenceCostScratch = new();

        /// <summary>
        /// Cached result of the last OT solve for reuse in EvaluateAdjointSource.
        /// </summary>
        private OptimalTransportResult? _last;

        private readonly object _costLock = new();

        public ConductivityAwareW2Metric(Config? config = null)
        {
            _config = config ?? new Config();
            _alphaCurrent = 0.0;
        }

        /// <summary>
        /// Evaluate J_alpha for the given discretization and data.
        /// Returns the scalar misfit J_alpha.
        /// </summary>
        public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (discretization == null) throw new ArgumentNullException(nameof(discretization));
            if (measured == null) throw new ArgumentNullException(nameof(measured));
            if (simulated == null) throw new ArgumentNullException(nameof(simulated));

            var result = SolveOt(discretization, measured, simulated);
            _last = result;
            return result.Cost;
        }

        /// <summary>
        /// Evaluate the gradient of J_alpha with respect to the simulated data.
        /// This is the adjoint source term fed into the PDE-level adjoint solve.
        /// </summary>
        public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (_last != null && _last.Matches(measured, simulated))
                return (double[])_last.Gradient.Clone();

            var result = SolveOt(discretization, measured, simulated);
            _last = result;
            return (double[])result.Gradient.Clone();
        }

        /// <summary>
        /// Main entry: measure W2-based misfit between measured and simulated data.
        /// Handles:
        /// - amplitude data (absolute electrode potentials) and
        /// - potential-difference data (channels).
        /// </summary>
        private OptimalTransportResult SolveOt(IDiscretization discretization, double[] measured, double[] simulated)
        {
            var mesh = discretization.GetDiscretization();
            var electrodes = discretization.GetElectrodes().OrderBy(e => e.Id).ToList();
            if (electrodes.Count == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, Array.Empty<double>());

            _maxElectrodes = Math.Max(_maxElectrodes, electrodes.Count);

            // Coordinate mapping for electrodes depends on discretization type.
            Func<Electrode, (double x, double y)> coord = mesh switch
            {
                FEMMesh fem => e => GetCoord(fem, e),
                LBMGrid lbm => e => e is LBMElectrode lbmElectrode
                    ? ToXY(lbm, lbmElectrode.GridId)
                    : throw new InvalidOperationException("Expected LBMElectrode for LBMGrid discretization."),
                _ => throw new NotSupportedException(
                    $"Conductivity-aware W2 requires FEMMesh or LBMGrid discretizations (got {mesh.GetType().Name}).")
            };

            // Decide whether we are in amplitude or potential-difference representation.
            var pattern = Workspace.GetMeasurementPattern();
            bool usingDifferences = pattern?.Representation == MeasurementRepresentation.PotentialDifference;
            if (!usingDifferences)
            {
                // Fallback heuristic if pattern is missing.
                var measuring = electrodes.Where(e => e.IsMeasuring).OrderBy(e => e.Id).ToList();
                var differenceElectrodes = measuring.Count > 0 ? (IReadOnlyList<Electrode>)measuring : electrodes;
                int expectedDifferenceLength = Math.Max(0, differenceElectrodes.Count - 1);
                usingDifferences = measured.Length == expectedDifferenceLength && simulated.Length == expectedDifferenceLength;
            }

            EnsureWarmup(usingDifferences ? measured.Length : electrodes.Count);
            _solveCounter++;

            OptimalTransportResult result = usingDifferences
                ? SolveDifferenceOt(mesh, electrodes, coord, pattern, measured, simulated)
                : SolveAmplitudeOt(mesh, electrodes, coord, measured, simulated);

            AdvanceWarmup();
            return result;
        }

        #region Warmup / alpha ramp

        private void EnsureWarmup(int measurementLength)
        {
            if (_warmupInitialized)
                return;

            int warmup = _config.WarmupSolves >= 0 ? _config.WarmupSolves : measurementLength;
            _warmupSolvesRemaining = Math.Max(0, warmup);
            _warmupInitialized = true;
            _useConductivityAware = _warmupSolvesRemaining <= 0;
        }

        private void AdvanceWarmup()
        {
            if (_warmupSolvesRemaining > 0)
            {
                _warmupSolvesRemaining--;
                if (_warmupSolvesRemaining == 0)
                    _useConductivityAware = true;
            }
        }

        #endregion

        #region Amplitude data OT

        /// <summary>
        /// Solve the OT problem for amplitude data (absolute electrode potentials).
        /// Implements:
        ///   J_alpha = (1-alpha) * 1/2 W2^2_geo + alpha * 1/2 W2^2_phys.
        /// </summary>
        private OptimalTransportResult SolveAmplitudeOt(
            Discretization mesh,
            IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord,
            double[] measured,
            double[] simulated)
        {
            int n = Math.Min(measured.Length, electrodes.Count);

            // Exclude NaNs/Infs from both distributions.
            var include = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (double.IsFinite(measured[i]) && double.IsFinite(simulated[i]))
                    include.Add(i);
            }

            if (include.Count == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[electrodes.Count]);

            // Build histograms and electrode coordinates restricted to "include".
            var (aRaw, aLoc, aMap) = BuildDistribution(simulated, electrodes, getCoord, include);
            var (bRaw, bLoc, _) = BuildDistribution(measured, electrodes, getCoord, include);

            if (aRaw.Length == 0 || bRaw.Length == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[electrodes.Count]);

            // --- 1) Geometric W2 (always computed) -------------------------
            // Uses c_geo(i,j) = |x_i - x_j|^2.
            var resGeo = w2_misfit_and_grad(aRaw, bRaw, aLoc, bLoc);

            // Map geometric gradient from histogram bins back to electrode index space.
            var gradGeoElectrodes = new double[electrodes.Count];
            foreach (var (srcIdx, electrodeIdx) in aMap)
                gradGeoElectrodes[electrodeIdx] = resGeo.Grad[srcIdx];

            // If we are still in warmup or alpha is ~0, return purely geometric W2.
            double alpha = (_useConductivityAware && _cPhysAmp != null) ? _alphaCurrent : 0.0;
            alpha = Math.Clamp(alpha, 0.0, 1.0);

            if (alpha <= 0.0)
            {
                return new OptimalTransportResult(measured, simulated, resGeo.Cost, gradGeoElectrodes);
            }

            // --- 2) Conductivity-aware W2 (when enabled) ------------------
            var sigma = ExtractConductivities(mesh);
            UpdateGroundCostIfNeeded(mesh, sigma, electrodes, getCoord);

            if (_cPhysAmp == null)
            {
                // Safety fallback: if conductivity matrix is missing, use geometric only.
                return new OptimalTransportResult(measured, simulated, resGeo.Cost, gradGeoElectrodes);
            }

            // Restrict conductivity-aware cost matrix to the included indices.
            var costPhys = GetAmplitudePhysCost(include);

            // Solve OT with conductivity-aware ground cost C_sigma.
            var resPhys = w2_misfit_and_grad(aRaw, bRaw, costPhys);

            // Map phys gradient back to electrode space.
            var gradPhysElectrodes = new double[electrodes.Count];
            foreach (var (srcIdx, electrodeIdx) in aMap)
                gradPhysElectrodes[electrodeIdx] = resPhys.Grad[srcIdx];

            // --- 3) Convex combination J_alpha = (1-alpha)*J_geo + alpha*J_phys ---

            double blendedCost = (1.0 - alpha) * resGeo.Cost + alpha * resPhys.Cost;
            var blendedGrad = new double[electrodes.Count];
            for (int i = 0; i < electrodes.Count; i++)
                blendedGrad[i] = (1.0 - alpha) * gradGeoElectrodes[i] + alpha * gradPhysElectrodes[i];

            return new OptimalTransportResult(measured, simulated, blendedCost, blendedGrad);
        }

        #endregion

        #region Difference data OT

        /// <summary>
        /// Solve the OT problem for potential-difference data (channels).
        /// Uses the signed-histogram splitting approach:
        /// - Separate positive and negative parts
        /// - Solve two OT problems for each (geo and phys)
        /// - Combine with appropriate mass weights
        /// - Finally form the convex blend J_alpha.
        /// </summary>
        private OptimalTransportResult SolveDifferenceOt(
            Discretization mesh,
            IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord,
            MeasurementPattern? pattern,
            double[] measured,
            double[] simulated)
        {
            int differenceCount = measured.Length;

            // Exclude NaNs/Infs.
            var include = new List<int>(differenceCount);
            for (int i = 0; i < differenceCount; i++)
            {
                if (double.IsFinite(measured[i]) && double.IsFinite(simulated[i]))
                    include.Add(i);
            }

            if (include.Count == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

            var (aRaw, aLoc, aMap, leftIndices) = BuildDifferenceDistribution(simulated, electrodes, getCoord, include, pattern);
            var (bRaw, bLoc, _, _) = BuildDifferenceDistribution(measured, electrodes, getCoord, include, pattern);

            if (aRaw.Length == 0 || bRaw.Length == 0)
                return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

            int m = aRaw.Length;
            var gradOut = new double[differenceCount];

            // --- 1) Split into positive and negative parts -----------------
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

            // --- 2) Geometric W2 for plus and minus parts -----------------
            var resPlusGeo = w2_misfit_and_grad(aPlus, bPlus, aLoc, bLoc);
            var resMinusGeo = w2_misfit_and_grad(aMinus, bMinus, aLoc, bLoc);

            double massPlus = aPlus.Sum();
            double massMinus = aMinus.Sum();

            double[] gradSignedGeo = new double[m];
            for (int i = 0; i < m; i++)
                gradSignedGeo[i] = massPlus * resPlusGeo.Grad[i] - massMinus * resMinusGeo.Grad[i];

            // We will compute the conductivity-aware counterpart only if needed.
            double alpha = (_useConductivityAware && _cPhysDiff != null) ? _alphaCurrent : 0.0;
            alpha = Math.Clamp(alpha, 0.0, 1.0);

            if (alpha <= 0.0)
            {
                // Purely geometric W2.
                double meanGeo = gradSignedGeo.Sum() / m;
                for (int i = 0; i < m; i++)
                    gradSignedGeo[i] -= meanGeo;

                foreach (var (srcIdx, diffIdx) in aMap)
                    gradOut[diffIdx] = gradSignedGeo[srcIdx];

                double massTotalGeo = massPlus + massMinus + Tiny;
                double costGeo1 = (massPlus * resPlusGeo.Cost + massMinus * resMinusGeo.Cost) / massTotalGeo;

                return new OptimalTransportResult(measured, simulated, costGeo1, gradOut);
            }

            // --- 3) Conductivity-aware W2 for plus and minus parts --------
            var sigma = ExtractConductivities(mesh);
            UpdateGroundCostIfNeeded(mesh, sigma, electrodes, getCoord);

            if (_cPhysDiff == null)
            {
                // Safety fallback: geometric only.
                double meanGeo = gradSignedGeo.Sum() / m;
                for (int i = 0; i < m; i++)
                    gradSignedGeo[i] -= meanGeo;

                foreach (var (srcIdx, diffIdx) in aMap)
                    gradOut[diffIdx] = gradSignedGeo[srcIdx];

                double massTotalGeo = massPlus + massMinus + Tiny;
                double costGeo1 = (massPlus * resPlusGeo.Cost + massMinus * resMinusGeo.Cost) / massTotalGeo;

                return new OptimalTransportResult(measured, simulated, costGeo1, gradOut);
            }

            var costPhysMatrix = GetDifferencePhysCost(leftIndices);

            var resPlusPhys = w2_misfit_and_grad(aPlus, bPlus, costPhysMatrix);
            var resMinusPhys = w2_misfit_and_grad(aMinus, bMinus, costPhysMatrix);

            double[] gradSignedPhys = new double[m];
            for (int i = 0; i < m; i++)
                gradSignedPhys[i] = massPlus * resPlusPhys.Grad[i] - massMinus * resMinusPhys.Grad[i];

            // --- 4) Convex combination of signed gradients ----------------
            double[] gradSigned = new double[m];
            for (int i = 0; i < m; i++)
                gradSigned[i] = (1.0 - alpha) * gradSignedGeo[i] + alpha * gradSignedPhys[i];

            // Remove mean to enforce gauge (sum~0 over bins).
            double mean = gradSigned.Sum() / m;
            for (int i = 0; i < m; i++)
                gradSigned[i] -= mean;

            foreach (var (srcIdx, diffIdx) in aMap)
                gradOut[diffIdx] = gradSigned[srcIdx];

            // --- 5) Convex combination of costs ---------------------------
            double massTotal = massPlus + massMinus + Tiny;
            double costGeo = (massPlus * resPlusGeo.Cost + massMinus * resMinusGeo.Cost) / massTotal;
            double costPhys = (massPlus * resPlusPhys.Cost + massMinus * resMinusPhys.Cost) / massTotal;
            double blendedCost = (1.0 - alpha) * costGeo + alpha * costPhys;

            return new OptimalTransportResult(measured, simulated, blendedCost, gradOut);
        }

        #endregion

        #region Conductivity extraction & ground cost building

        /// <summary>
        /// Extract element conductivities as a flat array from the discretization.
        /// </summary>
        private static double[] ExtractConductivities(Discretization mesh)
        {
            return mesh switch
            {
                FEMMesh fem => fem.ElementsTyped.Select(e => e.Conductivity).ToArray(),
                LBMGrid lbm => lbm.ElementsTyped.Select(e => e.Conductivity).ToArray(),
                _ => Array.Empty<double>()
            };
        }

        /// <summary>
        /// Build conductivity-aware ground costs C_sigma(i,j) whenever sigma changes sufficiently
        /// or after a configurable number of solves.
        ///
        /// Steps:
        /// - Build geometric cost c_geo(i,j) = |x_i - x_j|^2 (once per electrode set)
        /// - Build conductive geodesic distances d_sigma(i,j) via Dijkstra on FEM/LBM graph
        /// - Scale d_sigma^2 so its median matches the median of c_geo
        /// - Store C_sigma(i,j) = scale * d_sigma(i,j)^2 in _cPhysAmp/_cPhysDiff
        /// - Update alpha with a smooth ramp towards TargetAlpha.
        /// </summary>
        private void UpdateGroundCostIfNeeded(
            Discretization mesh,
            double[] sigma,
            IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord)
        {
            if (!_useConductivityAware)
                return;

            bool rebuild = _cPhysAmp == null || _euclideanCost == null;
            if (!rebuild && _lastCostBuildIter >= 0 &&
                (_solveCounter - _lastCostBuildIter) >= Math.Max(1, _config.RecomputeEvery))
                rebuild = true;

            if (!rebuild && sigma.Length > 0)
            {
                if (_sigmaSnapshot == null || _sigmaSnapshot.Length != sigma.Length)
                {
                    rebuild = true;
                }
                else
                {
                    double change = 0.0;
                    for (int i = 0; i < sigma.Length; i++)
                        change += Math.Abs(sigma[i] - _sigmaSnapshot[i]);
                    if (change >= _config.SigmaChangeTolerance)
                        rebuild = true;
                }
            }

            if (!rebuild)
                return;

            lock (_costLock)
            {
                bool need = _cPhysAmp == null || _euclideanCost == null;
                if (!need && _lastCostBuildIter >= 0 &&
                    (_solveCounter - _lastCostBuildIter) >= Math.Max(1, _config.RecomputeEvery))
                    need = true;

                if (!need && sigma.Length > 0)
                {
                    if (_sigmaSnapshot == null || _sigmaSnapshot.Length != sigma.Length)
                    {
                        need = true;
                    }
                    else
                    {
                        double change = 0.0;
                        for (int i = 0; i < sigma.Length; i++)
                            change += Math.Abs(sigma[i] - _sigmaSnapshot[i]);
                        if (change >= _config.SigmaChangeTolerance)
                            need = true;
                    }
                }

                if (!need)
                    return;

                int k = electrodes.Count;

                // Electrode positions (for geometric cost).
                var positions = new (double x, double y)[k];
                for (int i = 0; i < k; i++)
                    positions[i] = getCoord(electrodes[i]);

                // Geometric cost: c_geo(i,j) = |x_i - x_j|^2.
                _euclideanCost ??= ComputeEuclideanCost(positions);

                // Conductivity-aware distances: d_sigma(i,j).
                double[,] dsigma = mesh switch
                {
                    FEMMesh fem => ConductivityAwareGroundCostBuilder.BuildFromFemMesh(
                        fem, sigma, electrodes, getCoord, _config.GeodesicBeta),
                    LBMGrid lbm => ConductivityAwareGroundCostBuilder.BuildFromLbmGrid(
                        lbm, BuildLbmSigma(lbm, sigma), electrodes, getCoord, _config.GeodesicBeta),
                    _ => throw new NotSupportedException(
                        $"Conductivity-aware ground cost not implemented for {mesh.GetType().Name}.")
                };

                // Scale d_sigma^2 so its median matches median(c_geo).
                double medE = ComputeMedian(_euclideanCost);
                double medD = ComputeMedianSquared(dsigma); // median of d_sigma^2 (ignoring tiny distances).
                _scale = medD <= Tiny ? 1.0 : medE / medD;

                // Alpha ramp towards target value.
                double targetAlpha = Math.Clamp(_config.TargetAlpha, 0.0, 1.0);
                if (_config.EnableAlphaRamp && targetAlpha > _alphaCurrent)
                {
                    double delta = Math.Max(0.05 * targetAlpha, 0.5 * (targetAlpha - _alphaCurrent));
                    _alphaCurrent = Math.Min(targetAlpha, _alphaCurrent + delta);
                }
                else
                {
                    _alphaCurrent = targetAlpha;
                }

                // Build conductivity-aware cost C_sigma(i,j) = scale * d_sigma(i,j)^2.
                var phys = new double[k, k];
                for (int i = 0; i < k; i++)
                {
                    for (int j = 0; j < k; j++)
                    {
                        if (i == j)
                        {
                            phys[i, j] = 0.0;
                            continue;
                        }

                        double d = dsigma[i, j];
                        double d2 = d * d;
                        double value = _scale * d2;
                        phys[i, j] = value;
                    }
                }

                _cPhysAmp = phys;
                _cPhysDiff = phys;
                _lastCostBuildIter = _solveCounter;

                if (sigma.Length > 0)
                {
                    _sigmaSnapshot = _sigmaSnapshot != null && _sigmaSnapshot.Length == sigma.Length
                        ? _sigmaSnapshot
                        : new double[sigma.Length];
                    Array.Copy(sigma, _sigmaSnapshot, sigma.Length);
                }
            }
        }

        private static double[,] BuildLbmSigma(LBMGrid grid, double[] flat)
        {
            var sigma = new double[grid.Nx, grid.Ny];
            for (int y = 0; y < grid.Ny; y++)
            {
                for (int x = 0; x < grid.Nx; x++)
                {
                    int id = y * grid.Nx + x;
                    double value = id < flat.Length ? flat[id] : 1.0;
                    sigma[x, y] = value;
                }
            }
            return sigma;
        }

        /// <summary>
        /// Compute geometric squared distances between electrode positions.
        /// </summary>
        private static double[,] ComputeEuclideanCost(IReadOnlyList<(double x, double y)> positions)
        {
            int k = positions.Count;
            var cost = new double[k, k];
            for (int i = 0; i < k; i++)
            {
                cost[i, i] = 0.0;
                for (int j = i + 1; j < k; j++)
                {
                    double dx = positions[i].x - positions[j].x;
                    double dy = positions[i].y - positions[j].y;
                    double d2 = dx * dx + dy * dy;
                    cost[i, j] = d2;
                    cost[j, i] = d2;
                }
            }
            return cost;
        }

        /// <summary>
        /// Median of off-diagonal entries of a symmetric matrix.
        /// </summary>
        private static double ComputeMedian(double[,] matrix)
        {
            int n = matrix.GetLength(0);
            if (n <= 1)
                return 0.0;

            int count = n * (n - 1) / 2;
            var values = ArrayPool<double>.Shared.Rent(Math.Max(1, count));
            try
            {
                int idx = 0;
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        double v = matrix[i, j];
                        if (double.IsNaN(v))
                            continue;
                        values[idx++] = v;
                    }
                }
                if (idx == 0)
                    return 0.0;
                Array.Sort(values, 0, idx);
                int mid = idx / 2;
                if ((idx & 1) == 0)
                    return 0.5 * (values[mid - 1] + values[mid]);
                return values[mid];
            }
            finally
            {
                ArrayPool<double>.Shared.Return(values, clearArray: true);
            }
        }

        /// <summary>
        /// Median of squares d^2 of off-diagonal entries (ignoring very tiny distances).
        /// </summary>
        private static double ComputeMedianSquared(double[,] matrix)
        {
            int n = matrix.GetLength(0);
            if (n <= 1)
                return 0.0;

            int count = n * (n - 1) / 2;
            var values = ArrayPool<double>.Shared.Rent(Math.Max(1, count));
            try
            {
                int idx = 0;
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        double d = matrix[i, j];
                        if (d <= TinyDistance)
                            continue;
                        double d2 = d * d;
                        values[idx++] = d2;
                    }
                }
                if (idx == 0)
                    return 0.0;
                Array.Sort(values, 0, idx);
                int mid = idx / 2;
                if ((idx & 1) == 0)
                    return 0.5 * (values[mid - 1] + values[mid]);
                return values[mid];
            }
            finally
            {
                ArrayPool<double>.Shared.Return(values, clearArray: true);
            }
        }

        #endregion

        #region Submatrix helpers

        private double[,] GetAmplitudePhysCost(List<int> include)
        {
            var baseMatrix = _cPhysAmp ?? throw new InvalidOperationException("Conductivity-aware ground cost not built yet.");
            int size = include.Count;
            var scratch = GetScratchMatrix(_amplitudeCostScratch, size);
            for (int i = 0; i < size; i++)
            {
                int row = include[i];
                for (int j = 0; j < size; j++)
                {
                    int col = include[j];
                    scratch[i, j] = baseMatrix[row, col];
                }
            }
            return scratch;
        }

        private double[,] GetDifferencePhysCost(int[] leftIndices)
        {
            var baseMatrix = _cPhysDiff ?? throw new InvalidOperationException("Conductivity-aware ground cost not built yet.");
            int size = leftIndices.Length;
            var scratch = GetScratchMatrix(_differenceCostScratch, size);
            for (int i = 0; i < size; i++)
            {
                int row = leftIndices[i];
                for (int j = 0; j < size; j++)
                {
                    int col = leftIndices[j];
                    scratch[i, j] = baseMatrix[row, col];
                }
            }
            return scratch;
        }

        private static double[,] GetScratchMatrix(Dictionary<int, double[,]> pool, int size)
        {
            if (!pool.TryGetValue(size, out var matrix))
            {
                matrix = new double[size, size];
                pool[size] = matrix;
            }
            return matrix;
        }

        #endregion

        #region Histogram building helpers

        private static (double[] raw, (double x, double y)[] loc, List<(int srcIdx, int electrodeIdx)> map)
            BuildDistribution(double[] raw, IReadOnlyList<Electrode> electrodes, Func<Electrode, (double x, double y)> getCoord, List<int> include)
        {
            var values = new List<double>(include.Count);
            var coords = new List<(double, double)>(include.Count);
            var mapping = new List<(int, int)>(include.Count);

            foreach (int idx in include)
            {
                if (idx < 0 || idx >= raw.Length)
                    continue;
                double value = raw[idx];
                if (!double.IsFinite(value))
                    continue;
                var electrode = electrodes[idx];
                values.Add(value);
                coords.Add(getCoord(electrode));
                mapping.Add((values.Count - 1, idx));
            }

            return (values.ToArray(), coords.ToArray(), mapping);
        }

        private static (double[] raw,
                        (double x, double y)[] loc,
                        List<(int srcIdx, int diffIdx)> map,
                        int[] leftElectrodes)
            BuildDifferenceDistribution(double[] raw,
                                        IReadOnlyList<Electrode> electrodes,
                                        Func<Electrode, (double x, double y)> getCoord,
                                        List<int> include,
                                        MeasurementPattern? pattern)
        {
            var values = new List<double>(include.Count);
            var coords = new List<(double, double)>(include.Count);
            var mapping = new List<(int, int)>(include.Count);
            var leftIndices = new List<int>(include.Count);

            foreach (int diffIdx in include)
            {
                if (diffIdx < 0 || diffIdx >= raw.Length)
                    continue;

                double value = raw[diffIdx];
                if (!double.IsFinite(value))
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
                    // Simple fallback pairing if pattern is missing.
                    left = diffIdx % electrodes.Count;
                    right = (diffIdx + 1) % electrodes.Count;
                }

                if (left < 0 || left >= electrodes.Count)
                    continue;
                if (right < 0 || right >= electrodes.Count)
                    continue;

                values.Add(value);
                coords.Add(getCoord(electrodes[left]));
                mapping.Add((values.Count - 1, diffIdx));
                leftIndices.Add(left);
            }

            return (values.ToArray(), coords.ToArray(), mapping, leftIndices.ToArray());
        }

        #endregion

        #region Coordinate helpers

        private static (double x, double y) ToXY(LBMGrid grid, int gridId)
        {
            int x = gridId % grid.Nx;
            int y = gridId / grid.Nx;
            return (x, y);
        }

        private static (double x, double y) GetCoord(FEMMesh mesh, Electrode electrode)
        {
            if (electrode is not FEMElectrode femElectrode)
                throw new InvalidOperationException("Expected FEMElectrode when using FEMMesh discretization.");

            // For patch electrodes: average the coordinates of all associated FEM vertices.
            if (!femElectrode.PointElectrode && femElectrode.FEMVertexIds.Count > 0)
            {
                double sx = 0.0;
                double sy = 0.0;
                int count = 0;
                foreach (int id in femElectrode.FEMVertexIds)
                {
                    var vertex = mesh.Vertices.FirstOrDefault(v => v.GlobalId == id);
                    if (vertex == null)
                        continue;
                    sx += vertex.X;
                    sy += vertex.Y;
                    count++;
                }
                if (count > 0)
                    return (sx / count, sy / count);
            }

            // Fallback: anchor vertex.
            var anchor = mesh.Vertices.FirstOrDefault(v => v.GlobalId == femElectrode.MeshId)
                ?? mesh.Vertices.First();
            return (anchor.X, anchor.Y);
        }

        #endregion

        #region Result container

        private sealed class OptimalTransportResult
        {
            private readonly double[] _measured;
            private readonly double[] _simulated;

            public double Cost { get; }
            public double[] Gradient { get; }

            public OptimalTransportResult(double[] measured, double[] simulated, double cost, double[] gradient)
            {
                _measured = measured;
                _simulated = simulated;
                Cost = cost;
                Gradient = gradient;
            }

            public bool Matches(double[] measured, double[] simulated)
                => ReferenceEquals(_measured, measured) && ReferenceEquals(_simulated, simulated);
        }

        #endregion

        #region OT primitive (value + gradient w.r.t. source histogram)

        public sealed class OTResult
        {
            public double Cost { get; }
            public double[] Grad { get; }
            public double[,] Plan { get; }
            public double[] Phi { get; }
            public double[] Psi { get; }

            public OTResult(double cost, double[] grad, double[,] plan, double[] phi, double[] psi)
            {
                Cost = cost;
                Grad = grad;
                Plan = plan;
                Phi = phi;
                Psi = psi;
            }
        }

        /// <summary>
        /// Compute 1/2 W2^2 between mPred and dObs with geometric cost c(i,j)=|x_i-x_j|^2
        /// and its gradient with respect to the (raw) source mPred.
        ///
        /// Internally:
        /// - Applies the normalization m_raw -> mu as in (4.1.3)
        /// - Solves the primal LP with OR-Tools GLOP
        /// - Reads dual potentials phi, psi from row/column duals
        /// - Uses Danskin: d/dmu (1/2 W2^2) = 1/2 phi*
        /// - Applies chain rule back to mPred (including normalization).
        /// </summary>
        public static OTResult w2_misfit_and_grad(double[] mPred, double[] dObs,
            (double x, double y)[] x, (double x, double y)[] y)
        {
            if (mPred.Length != x.Length || dObs.Length != y.Length)
                throw new ArgumentException("Mass and coordinate arrays must align.");

            double[] a = (double[])mPred.Clone();
            double[] b = (double[])dObs.Clone();

            // Replace NaNs/Infs with zero.
            for (int i = 0; i < a.Length; i++)
                if (!double.IsFinite(a[i]))
                    a[i] = 0.0;
            for (int j = 0; j < b.Length; j++)
                if (!double.IsFinite(b[j]))
                    b[j] = 0.0;

            // Shift so minimum is zero, then clip negatives (approximate derivative of max(...,0)).
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
                if (a[i] < 0) a[i] = 0.0;
            for (int j = 0; j < b.Length; j++)
                if (b[j] < 0) b[j] = 0.0;

            // Normalize to probability simplex.
            double sumA = a.Sum();
            double sumB = b.Sum();
            if (sumA <= Tiny || sumB <= Tiny)
                return new OTResult(0.0, new double[a.Length], new double[a.Length, b.Length],
                    new double[a.Length], new double[b.Length]);

            for (int i = 0; i < a.Length; i++) a[i] /= sumA;
            for (int j = 0; j < b.Length; j++) b[j] /= sumB;

            int m = a.Length, n = b.Length;

            var solver = Solver.CreateSolver("GLOP") ?? throw new InvalidOperationException("OR-Tools LP solver 'GLOP' not available.");

            var plan = new Variable[m, n];
            var row = new Constraint[m];
            var col = new Constraint[n];

            for (int i = 0; i < m; i++) row[i] = solver.MakeConstraint(a[i], a[i], $"row[{i}]");
            for (int j = 0; j < n; j++) col[j] = solver.MakeConstraint(b[j], b[j], $"col[{j}]");

            var obj = solver.Objective();
            for (int i = 0; i < m; i++)
            {
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
            }

            obj.SetMinimization();
            var status = solver.Solve();
            if (status != Solver.ResultStatus.OPTIMAL)
                throw new InvalidOperationException($"W2 primal LP not optimal. Status={status}");

            double[,] P = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    P[i, j] = plan[i, j].SolutionValue();

            // 1/2 W2^2
            double cost = 0.5 * obj.Value();

            // Dual potentials.
            double[] phi = new double[m];
            double[] psi = new double[n];
            for (int i = 0; i < m; i++) phi[i] = row[i].DualValue();
            for (int j = 0; j < n; j++) psi[j] = col[j].DualValue();

            // Gradient w.r.t. normalized source mu: 1/2 phi*, then subtract mean over mu.
            double[] grad = new double[m];
            for (int i = 0; i < m; i++) grad[i] = 0.5 * phi[i];
            double mean = 0.0;
            for (int i = 0; i < m; i++) mean += grad[i] * a[i];
            for (int i = 0; i < m; i++) grad[i] -= mean;

            // Chain rule back to raw mPred (approximate normalization derivative).
            double[] gradRaw = new double[m];
            for (int i = 0; i < m; i++) gradRaw[i] = grad[i] / sumA;

            return new OTResult(cost, gradRaw, P, phi, psi);
        }

        /// <summary>
        /// Same as above but uses an explicit cost matrix (e.g., conductivity-aware C_sigma).
        /// </summary>
        public static OTResult w2_misfit_and_grad(double[] mPred, double[] dObs, double[,] costMatrix)
        {
            if (mPred.Length != dObs.Length)
                throw new ArgumentException("Mass arrays must have the same length when using an explicit cost matrix.");
            if (costMatrix.GetLength(0) != mPred.Length || costMatrix.GetLength(1) != dObs.Length)
                throw new ArgumentException("Cost matrix dimensions must match the histogram sizes.");

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
                for (int i = 0; i < a.Length; i++) a[i] -= minA;
            if (minB < 0)
                for (int j = 0; j < b.Length; j++) b[j] -= minB;

            for (int i = 0; i < a.Length; i++) if (a[i] < 0) a[i] = 0.0;
            for (int j = 0; j < b.Length; j++) if (b[j] < 0) b[j] = 0.0;

            double sumA = a.Sum();
            double sumB = b.Sum();
            if (sumA <= Tiny || sumB <= Tiny)
                return new OTResult(0.0, new double[a.Length], new double[a.Length, b.Length],
                    new double[a.Length], new double[b.Length]);

            for (int i = 0; i < a.Length; i++) a[i] /= sumA;
            for (int j = 0; j < b.Length; j++) b[j] /= sumB;

            int m = a.Length, n = b.Length;
            var solver = Solver.CreateSolver("GLOP") ?? throw new InvalidOperationException("OR-Tools LP solver 'GLOP' not available.");

            var plan = new Variable[m, n];
            var row = new Constraint[m];
            var col = new Constraint[n];
            for (int i = 0; i < m; i++) row[i] = solver.MakeConstraint(a[i], a[i], $"row[{i}]");
            for (int j = 0; j < n; j++) col[j] = solver.MakeConstraint(b[j], b[j], $"col[{j}]");

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
                throw new InvalidOperationException($"W2 primal LP not optimal. Status={status}");

            double[,] P = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    P[i, j] = plan[i, j].SolutionValue();

            double cost = 0.5 * obj.Value();
            double[] phi = new double[m];
            double[] psi = new double[n];
            for (int i = 0; i < m; i++) phi[i] = row[i].DualValue();
            for (int j = 0; j < n; j++) psi[j] = col[j].DualValue();

            double[] grad = new double[m];
            for (int i = 0; i < m; i++) grad[i] = 0.5 * phi[i];
            double mean = 0.0;
            for (int i = 0; i < m; i++) mean += grad[i] * a[i];
            for (int i = 0; i < m; i++) grad[i] -= mean;

            double[] gradRaw = new double[m];
            for (int i = 0; i < m; i++) gradRaw[i] = grad[i] / sumA;

            return new OTResult(cost, gradRaw, P, phi, psi);
        }

        #endregion

        #region Conductivity-aware ground cost builder (geodesic, FEM/LBM)

        private static class ConductivityAwareGroundCostBuilder
        {
            /// <summary>
            /// Build conductivity-aware distances d_sigma(i,j) on a FEM mesh:
            /// - Graph nodes: FEM vertices
            /// - Edge cost between adjacent vertices: length * sigma_edge^{-beta}
            ///   where sigma_edge is harmonic mean of neighboring elements.
            /// - d_sigma(i,j): shortest path over this weighted graph between electrode supports.
            /// </summary>
            public static double[,] BuildFromFemMesh(
                FEMMesh mesh,
                double[] elemSigma,
                IReadOnlyList<Electrode> electrodes,
                Func<Electrode, (double x, double y)> getCoord,
                double beta)
            {
                var vertices = mesh.Vertices;
                int nodeCount = vertices.Count;
                var idToIndex = new Dictionary<int, int>(nodeCount);
                for (int i = 0; i < nodeCount; i++)
                    idToIndex[vertices[i].GlobalId] = i;

                // Build edge list with length and conductivities of incident elements.
                var edgeMap = new Dictionary<(int u, int v), (double length, double sigma1, double sigma2)>();
                var elements = mesh.ElementsTyped;
                for (int eIdx = 0; eIdx < elements.Count; eIdx++)
                {
                    var element = elements[eIdx];
                    double sigma = eIdx < elemSigma.Length ? elemSigma[eIdx] : element.Conductivity;
                    var v = element.Vertices;
                    var triples = new (FEMVertex, FEMVertex)[]
                    {
                        (v[0], v[1]),
                        (v[1], v[2]),
                        (v[2], v[0])
                    };
                    foreach (var (va, vb) in triples)
                    {
                        int ia = idToIndex[va.GlobalId];
                        int ib = idToIndex[vb.GlobalId];
                        var key = ia < ib ? (ia, ib) : (ib, ia);
                        double dx = va.X - vb.X;
                        double dy = va.Y - vb.Y;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        if (!edgeMap.TryGetValue(key, out var data))
                        {
                            edgeMap[key] = (len, sigma, double.NaN);
                        }
                        else
                        {
                            edgeMap[key] = (data.length, data.sigma1, sigma);
                        }
                    }
                }

                // Build adjacency lists with geodesic edge cost.
                var neighbors = new List<int>[nodeCount];
                var costs = new List<double>[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                {
                    neighbors[i] = new List<int>();
                    costs[i] = new List<double>();
                }

                foreach (var (key, data) in edgeMap)
                {
                    double sigmaEdge;
                    if (!double.IsNaN(data.sigma2))
                    {
                        // Harmonic average of adjacent elements.
                        sigmaEdge = 2.0 * data.sigma1 * data.sigma2 / (data.sigma1 + data.sigma2 + SigmaEps);
                    }
                    else
                    {
                        sigmaEdge = data.sigma1;
                    }

                    sigmaEdge = Math.Max(sigmaEdge, SigmaEps);

                    // Edge cost ~ length * sigma_edge^{-beta}.
                    double cost = data.length * Math.Pow(sigmaEdge, -beta);

                    neighbors[key.u].Add(key.v);
                    costs[key.u].Add(cost);
                    neighbors[key.v].Add(key.u);
                    costs[key.v].Add(cost);
                }

                int[][] neighborArray = new int[nodeCount][];
                double[][] costArray = new double[nodeCount][];
                for (int i = 0; i < nodeCount; i++)
                {
                    neighborArray[i] = neighbors[i].ToArray();
                    costArray[i] = costs[i].ToArray();
                }

                // Map electrodes to sets of FEM vertices.
                int[][] electrodeNodes = new int[electrodes.Count][];
                for (int i = 0; i < electrodes.Count; i++)
                    electrodeNodes[i] = MapFemElectrode(mesh, electrodes[i], idToIndex, getCoord);

                return ComputeElectrodeDistances(electrodeNodes, neighborArray, costArray);
            }

            /// <summary>
            /// Build conductivity-aware distances d_sigma(i,j) on an LBM grid:
            /// - Graph nodes: grid cells
            /// - Edge cost between neighbors: sigma_edge^{-beta}
            /// - d_sigma(i,j): shortest path over this graph between electrode supports.
            /// </summary>
            public static double[,] BuildFromLbmGrid(
                LBMGrid grid,
                double[,] sigma,
                IReadOnlyList<Electrode> electrodes,
                Func<Electrode, (double x, double y)> getCoord,
                double beta)
            {
                int nodeCount = grid.Nx * grid.Ny;
                var neighbors = new List<int>[nodeCount];
                var costs = new List<double>[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                {
                    neighbors[i] = new List<int>(4);
                    costs[i] = new List<double>(4);
                }

                var directions = new (int dx, int dy)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
                for (int y = 0; y < grid.Ny; y++)
                {
                    for (int x = 0; x < grid.Nx; x++)
                    {
                        var element = grid.GetElementAt(x, y);
                        if (element.IsWall)
                            continue;

                        int id = y * grid.Nx + x;
                        foreach (var (dx, dy) in directions)
                        {
                            int nx = x + dx;
                            int ny = y + dy;
                            if (nx < 0 || nx >= grid.Nx || ny < 0 || ny >= grid.Ny)
                                continue;
                            var neighbor = grid.GetElementAt(nx, ny);
                            if (neighbor.IsWall)
                                continue;

                            int nid = ny * grid.Nx + nx;
                            double s1 = sigma[x, y];
                            double s2 = sigma[nx, ny];
                            double sigmaEdge = 2.0 * s1 * s2 / (s1 + s2 + SigmaEps);
                            sigmaEdge = Math.Max(sigmaEdge, SigmaEps);

                            // Edge cost ~ sigma_edge^{-beta}. For a regular grid, length is constant.
                            double cost = Math.Pow(sigmaEdge, -beta);

                            neighbors[id].Add(nid);
                            costs[id].Add(cost);
                        }
                    }
                }

                int[][] neighborArray = new int[nodeCount][];
                double[][] costArray = new double[nodeCount][];
                for (int i = 0; i < nodeCount; i++)
                {
                    neighborArray[i] = neighbors[i].ToArray();
                    costArray[i] = costs[i].ToArray();
                }

                int[][] electrodeNodes = new int[electrodes.Count][];
                for (int i = 0; i < electrodes.Count; i++)
                    electrodeNodes[i] = MapLbmElectrode(grid, electrodes[i]);

                return ComputeElectrodeDistances(electrodeNodes, neighborArray, costArray);
            }

            private static int[] MapFemElectrode(FEMMesh mesh, Electrode electrode,
                                                 Dictionary<int, int> idToIndex,
                                                 Func<Electrode, (double x, double y)> getCoord)
            {
                if (electrode is FEMElectrode fem)
                {
                    var nodes = new List<int>(fem.FEMVertexIds.Count + 1);
                    foreach (int id in fem.FEMVertexIds)
                    {
                        if (idToIndex.TryGetValue(id, out int idx))
                            nodes.Add(idx);
                    }
                    if (nodes.Count == 0 && idToIndex.TryGetValue(fem.MeshId, out int meshIdx))
                        nodes.Add(meshIdx);
                    if (nodes.Count > 0)
                        return nodes.ToArray();
                }

                // Fallback: nearest vertex by Euclidean distance.
                var pos = getCoord(electrode);
                double best = double.PositiveInfinity;
                int bestIdx = 0;
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    double dx = mesh.Vertices[i].X - pos.x;
                    double dy = mesh.Vertices[i].Y - pos.y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < best)
                    {
                        best = d2;
                        bestIdx = i;
                    }
                }
                return new[] { bestIdx };
            }

            private static int[] MapLbmElectrode(LBMGrid grid, Electrode electrode)
            {
                if (electrode is LBMElectrode lbm)
                    return new[] { lbm.GridId };
                return new[] { 0 };
            }

            /// <summary>
            /// Compute d_sigma(i,j) as minimum over all pairs of supporting nodes using Dijkstra.
            /// </summary>
            private static double[,] ComputeElectrodeDistances(
                int[][] electrodeNodes,
                int[][] neighbors,
                double[][] costs)
            {
                int nodeCount = neighbors.Length;
                int electrodeCount = electrodeNodes.Length;
                var result = new double[electrodeCount, electrodeCount];

                Parallel.For(0, electrodeCount, s =>
                {
                    var dist = Dijkstra(electrodeNodes[s], neighbors, costs, nodeCount);
                    for (int t = 0; t < electrodeCount; t++)
                    {
                        double best = double.PositiveInfinity;
                        var targets = electrodeNodes[t];
                        for (int k = 0; k < targets.Length; k++)
                        {
                            int node = targets[k];
                            double d = dist[node];
                            if (d < best)
                                best = d;
                        }
                        if (!double.IsFinite(best))
                            best = double.MaxValue;
                        best = Math.Max(best, TinyDistance);
                        result[s, t] = best;
                    }
                });

                // Symmetrize and zero diagonal.
                for (int i = 0; i < electrodeCount; i++)
                {
                    result[i, i] = 0.0;
                    for (int j = i + 1; j < electrodeCount; j++)
                    {
                        double sym = 0.5 * (result[i, j] + result[j, i]);
                        result[i, j] = sym;
                        result[j, i] = sym;
                    }
                }

                return result;
            }

            private static double[] Dijkstra(
                int[] sources,
                int[][] neighbors,
                double[][] costs,
                int nodeCount)
            {
                var dist = new double[nodeCount];
                Array.Fill(dist, double.PositiveInfinity);
                var queue = new PriorityQueue<int, double>();

                foreach (int src in sources)
                {
                    if (src < 0 || src >= nodeCount)
                        continue;
                    dist[src] = 0.0;
                    queue.Enqueue(src, 0.0);
                }

                while (queue.TryDequeue(out int node, out double current))
                {
                    if (current > dist[node])
                        continue;
                    var neigh = neighbors[node];
                    var weight = costs[node];
                    for (int k = 0; k < neigh.Length; k++)
                    {
                        int nb = neigh[k];
                        double alt = current + weight[k];
                        if (alt < dist[nb])
                        {
                            dist[nb] = alt;
                            queue.Enqueue(nb, alt);
                        }
                    }
                }

                return dist;
            }
        }

        #endregion
    }
}
