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

namespace Utility.Classes.Reconstruction.ErrorMetrics;

/// <summary>
/// Conductivity-aware Wasserstein-2 error metric.
/// </summary>
public sealed class ConductivityAwareW2Metric : IErrorMetric
{
    /// <summary>
    /// Public configuration knobs. Defaults follow the specification while keeping
    /// the original call sites unchanged.
    /// </summary>
    public sealed class Config
    {
        /// <summary>Target blend weight for the conductivity-aware ground cost.</summary>
        public double TargetAlpha { get; init; } = 0.25;

        /// <summary>How many OT solves elapse between ground-cost rebuilds.</summary>
        public int RecomputeEvery { get; init; } = 3;

        /// <summary>
        /// L1 change in σ that triggers an early rebuild of the lagged ground cost.
        /// </summary>
        public double SigmaChangeTolerance { get; init; } = 1e-3;

        /// <summary>Warm-up override. When negative the measurement length is used.</summary>
        public int WarmupSolves { get; init; } = -1;

        /// <summary>If true we gradually ramp α towards <see cref="TargetAlpha"/>.</summary>
        public bool EnableAlphaRamp { get; init; } = true;
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

    private double[,]? _euclideanCost;
    private double[,]? _cAmp;
    private double[,]? _cDiff;

    private double[]? _sigmaSnapshot;

    private readonly Dictionary<int, double[,]> _amplitudeCostScratch = new();
    private readonly Dictionary<int, double[,]> _differenceCostScratch = new();

    private OptimalTransportResult? _last;

    private readonly object _costLock = new();

    public ConductivityAwareW2Metric(Config? config = null)
    {
        _config = config ?? new Config();
        _alphaCurrent = 0.0;
    }
    public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
    {
        if (discretization == null) throw new ArgumentNullException(nameof(discretization));
        if (measured == null) throw new ArgumentNullException(nameof(measured));
        if (simulated == null) throw new ArgumentNullException(nameof(simulated));

        var result = SolveOt(discretization, measured, simulated);
        _last = result;
        return result.Cost;
    }

    public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
    {
        if (_last != null && _last.Matches(measured, simulated))
            return (double[])_last.Gradient.Clone();

        var result = SolveOt(discretization, measured, simulated);
        _last = result;
        return (double[])result.Gradient.Clone();
    }
    private OptimalTransportResult SolveOt(IDiscretization discretization, double[] measured, double[] simulated)
    {
        var mesh = discretization.GetDiscretization();
        var electrodes = discretization.GetElectrodes().OrderBy(e => e.Id).ToList();
        if (electrodes.Count == 0)
            return new OptimalTransportResult(measured, simulated, 0.0, Array.Empty<double>());

        _maxElectrodes = Math.Max(_maxElectrodes, electrodes.Count);

        Func<Electrode, (double x, double y)> coord = mesh switch
        {
            FEMMesh fem => e => GetCoord(fem, e),
            LBMGrid lbm => e => e is LBMElectrode lbmElectrode
                ? ToXY(lbm, lbmElectrode.GridId)
                : throw new InvalidOperationException("Expected LBMElectrode for LBMGrid discretization."),
            _ => throw new NotSupportedException($"Conductivity-aware W2 requires FEMMesh or LBMGrid discretizations (got {mesh.GetType().Name}).")
        };

        var pattern = Workspace.GetMeasurementPattern();
        bool usingDifferences = pattern?.Representation == MeasurementRepresentation.PotentialDifference;
        if (!usingDifferences)
        {
            // Fall back to legacy inference when pattern is absent.
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
    private OptimalTransportResult SolveAmplitudeOt(
        Discretization mesh,
        IReadOnlyList<Electrode> electrodes,
        Func<Electrode, (double x, double y)> getCoord,
        double[] measured,
        double[] simulated)
    {
        int n = Math.Min(measured.Length, electrodes.Count);
        var include = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (double.IsFinite(measured[i]) && double.IsFinite(simulated[i]))
                include.Add(i);
        }

        if (include.Count == 0)
            return new OptimalTransportResult(measured, simulated, 0.0, new double[electrodes.Count]);

        var (aRaw, aLoc, aMap) = BuildDistribution(simulated, electrodes, getCoord, include);
        var (bRaw, bLoc, _) = BuildDistribution(measured, electrodes, getCoord, include);

        if (aRaw.Length == 0 || bRaw.Length == 0)
            return new OptimalTransportResult(measured, simulated, 0.0, new double[electrodes.Count]);

        if (!_useConductivityAware)
        {
            var res = w2_misfit_and_grad(aRaw, bRaw, aLoc, bLoc);
            var grad = new double[electrodes.Count];
            foreach (var (srcIdx, electrodeIdx) in aMap)
                grad[electrodeIdx] = res.Grad[srcIdx];
            return new OptimalTransportResult(measured, simulated, res.Cost, grad);
        }

        var sigma = ExtractConductivities(mesh);
        UpdateGroundCostIfNeeded(mesh, sigma, electrodes, getCoord);

        var cost = GetAmplitudeCost(include);
        var resCa = w2_misfit_and_grad(aRaw, bRaw, cost);
        var gradCa = new double[electrodes.Count];
        foreach (var (srcIdx, electrodeIdx) in aMap)
            gradCa[electrodeIdx] = resCa.Grad[srcIdx];

        return new OptimalTransportResult(measured, simulated, resCa.Cost, gradCa);
    }
    private OptimalTransportResult SolveDifferenceOt(
        Discretization mesh,
        IReadOnlyList<Electrode> electrodes,
        Func<Electrode, (double x, double y)> getCoord,
        MeasurementPattern? pattern,
        double[] measured,
        double[] simulated)
    {
        int differenceCount = measured.Length;
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

        // Split signed histograms.
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

        if (!_useConductivityAware)
        {
            var resPlus = w2_misfit_and_grad(aPlus, bPlus, aLoc, bLoc);
            var resMinus = w2_misfit_and_grad(aMinus, bMinus, aLoc, bLoc);
            double massPlus = aPlus.Sum();
            double massMinus = aMinus.Sum();

            double[] gradSigned = new double[m];
            for (int i = 0; i < m; i++)
                gradSigned[i] = massPlus * resPlus.Grad[i] - massMinus * resMinus.Grad[i];

            double mean = gradSigned.Sum() / m;
            for (int i = 0; i < m; i++)
                gradSigned[i] -= mean;

            foreach (var (srcIdx, diffIdx) in aMap)
                gradOut[diffIdx] = gradSigned[srcIdx];

            double massTotal = massPlus + massMinus + Tiny;
            double cost = (massPlus * resPlus.Cost + massMinus * resMinus.Cost) / massTotal;
            return new OptimalTransportResult(measured, simulated, cost, gradOut);
        }

        var sigma = ExtractConductivities(mesh);
        UpdateGroundCostIfNeeded(mesh, sigma, electrodes, getCoord);

        var costMatrix = GetDifferenceCost(leftIndices);
        var resPlusCa = w2_misfit_and_grad(aPlus, bPlus, costMatrix);
        var resMinusCa = w2_misfit_and_grad(aMinus, bMinus, costMatrix);

        double massPlusCa = aPlus.Sum();
        double massMinusCa = aMinus.Sum();
        double[] gradSignedCa = new double[m];
        for (int i = 0; i < m; i++)
            gradSignedCa[i] = massPlusCa * resPlusCa.Grad[i] - massMinusCa * resMinusCa.Grad[i];

        double meanCa = gradSignedCa.Sum() / m;
        for (int i = 0; i < m; i++)
            gradSignedCa[i] -= meanCa;

        foreach (var (srcIdx, diffIdx) in aMap)
            gradOut[diffIdx] = gradSignedCa[srcIdx];

        double totalMass = massPlusCa + massMinusCa + Tiny;
        double costCa = (massPlusCa * resPlusCa.Cost + massMinusCa * resMinusCa.Cost) / totalMass;
        return new OptimalTransportResult(measured, simulated, costCa, gradOut);
    }
    private static double[] ExtractConductivities(Discretization mesh)
    {
        return mesh switch
        {
            FEMMesh fem => fem.ElementsTyped.Select(e => e.Conductivity).ToArray(),
            LBMGrid lbm => lbm.ElementsTyped.Select(e => e.Conductivity).ToArray(),
            _ => Array.Empty<double>()
        };
    }
    private void UpdateGroundCostIfNeeded(
        Discretization mesh,
        double[] sigma,
        IReadOnlyList<Electrode> electrodes,
        Func<Electrode, (double x, double y)> getCoord)
    {
        if (!_useConductivityAware)
            return;

        bool rebuild = _cAmp == null || _euclideanCost == null;
        if (!rebuild && _lastCostBuildIter >= 0 && (_solveCounter - _lastCostBuildIter) >= Math.Max(1, _config.RecomputeEvery))
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
            // Double check after acquiring the lock to avoid redundant rebuilds.
            bool need = _cAmp == null || _euclideanCost == null;
            if (!need && _lastCostBuildIter >= 0 && (_solveCounter - _lastCostBuildIter) >= Math.Max(1, _config.RecomputeEvery))
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
            var positions = new (double x, double y)[k];
            for (int i = 0; i < k; i++)
                positions[i] = getCoord(electrodes[i]);

            _euclideanCost = ComputeEuclideanCost(positions);

            double[,] dsigma = mesh switch
            {
                FEMMesh fem => ConductivityAwareGroundCostBuilder.BuildFromFemMesh(fem, sigma, electrodes, getCoord),
                LBMGrid lbm => ConductivityAwareGroundCostBuilder.BuildFromLbmGrid(lbm, BuildLbmSigma(lbm, sigma), electrodes, getCoord),
                _ => throw new NotSupportedException($"Conductivity-aware ground cost not implemented for {mesh.GetType().Name}.")
            };

            double medE = ComputeMedian(_euclideanCost);
            double medD = ComputeMedianSquared(dsigma);
            _scale = medD <= Tiny ? 1.0 : medE / medD;

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

            double[,] blended = new double[k, k];
            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < k; j++)
                {
                    double e = _euclideanCost[i, j];
                    double d = dsigma[i, j];
                    double d2 = d * d;
                    double value = (1.0 - _alphaCurrent) * e + _alphaCurrent * _scale * d2;
                    if (i == j)
                        value = 0.0;
                    blended[i, j] = value;
                }
            }

            _cAmp = blended;
            _cDiff = blended;
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
    private double[,] GetAmplitudeCost(List<int> include)
    {
        var baseMatrix = _cAmp ?? throw new InvalidOperationException("Conductivity-aware ground cost not built yet.");
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

    private double[,] GetDifferenceCost(int[] leftIndices)
    {
        var baseMatrix = _cDiff ?? throw new InvalidOperationException("Conductivity-aware ground cost not built yet.");
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

        var anchor = mesh.Vertices.FirstOrDefault(v => v.GlobalId == femElectrode.MeshId)
            ?? mesh.Vertices.First();
        return (anchor.X, anchor.Y);
    }
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
    public static OTResult w2_misfit_and_grad(double[] mPred, double[] dObs,
        (double x, double y)[] x, (double x, double y)[] y)
    {
        if (mPred.Length != x.Length || dObs.Length != y.Length)
            throw new ArgumentException("Mass and coordinate arrays must align.");

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
            if (a[i] < 0) a[i] = 0.0;
        for (int j = 0; j < b.Length; j++)
            if (b[j] < 0) b[j] = 0.0;

        double sumA = a.Sum();
        double sumB = b.Sum();
        if (sumA <= Tiny || sumB <= Tiny)
            return new OTResult(0.0, new double[a.Length], new double[a.Length, b.Length], new double[a.Length], new double[b.Length]);

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
            return new OTResult(0.0, new double[a.Length], new double[a.Length, b.Length], new double[a.Length], new double[b.Length]);

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
    private static class ConductivityAwareGroundCostBuilder
    {
        private sealed record EdgeAccumulator(double Length, double Sigma1, double Sigma2, bool HasSecond);

        public static double[,] BuildFromFemMesh(
            FEMMesh mesh,
            double[] elemSigma,
            IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord)
        {
            var vertices = mesh.Vertices;
            int nodeCount = vertices.Count;
            var idToIndex = new Dictionary<int, int>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
                idToIndex[vertices[i].GlobalId] = i;

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
                    sigmaEdge = 2.0 * data.sigma1 * data.sigma2 / (data.sigma1 + data.sigma2 + SigmaEps);
                }
                else
                {
                    sigmaEdge = data.sigma1;
                }

                sigmaEdge = Math.Max(sigmaEdge, SigmaEps);
                double cost = data.length / sigmaEdge;

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

            int[][] electrodeNodes = new int[electrodes.Count][];
            for (int i = 0; i < electrodes.Count; i++)
                electrodeNodes[i] = MapFemElectrode(mesh, electrodes[i], idToIndex, getCoord);

            return ComputeElectrodeDistances(electrodeNodes, neighborArray, costArray);
        }

        public static double[,] BuildFromLbmGrid(
            LBMGrid grid,
            double[,] sigma,
            IReadOnlyList<Electrode> electrodes,
            Func<Electrode, (double x, double y)> getCoord)
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
                        double cost = 1.0 / sigmaEdge;
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

        private static int[] MapFemElectrode(FEMMesh mesh, Electrode electrode, Dictionary<int, int> idToIndex, Func<Electrode, (double x, double y)> getCoord)
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

            // Fallback: snap to the nearest vertex by Euclidean distance.
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

        private static double[,] ComputeElectrodeDistances(int[][] electrodeNodes, int[][] neighbors, double[][] costs)
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

        private static double[] Dijkstra(int[] sources, int[][] neighbors, double[][] costs, int nodeCount)
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
}
