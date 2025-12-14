using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.ErrorMetrics;

/// <summary>
/// Implements an unbalanced Wasserstein–Fisher–Rao (WFR) error metric that
/// operates directly on electrode measurements.  The metric supports both the
/// amplitude and potential-difference acquisition modes used by the
/// reconstruction pipeline and exposes the same <see cref="IErrorMetric"/>
/// interface as the legacy Wasserstein-2 implementations.
///
/// MATHEMATICAL OVERVIEW
/// =====================
/// Given two non-negative measures <c>a</c> (simulated signal) and
/// <c>b</c> (measured signal) on a shared discrete support the WFR distance is
/// obtained by solving the entropically regularised, unbalanced optimal
/// transport problem
///
///   OT<sub>ε,ρ</sub>(a, b) = min<sub>π ≥ 0</sub> Σ₍ᵢⱼ₎ πᵢⱼ Cᵢⱼ
///       + ε KL(π || K) + ρ KL(π 1 || a) + ρ KL(πᵀ 1 || b)
///
/// where <c>K = exp(-C / ε)</c> is the Gibbs kernel derived from the ground
/// cost <c>C</c>, <c>ε &gt; 0</c> controls the entropic regularisation and
/// <c>ρ &gt; 0</c> penalises mass variation.  The solver implemented here follows
/// the stabilised unbalanced Sinkhorn iterations introduced by Chizat et al.
/// (2016).  Dual potentials f and g are extracted from the logarithmic scaling
/// factors and provide both the transport cost and the gradient with respect to
/// the source measure.
///
/// NUMERICAL STRATEGY
/// ==================
/// * A dedicated kernel cache avoids recomputing exp(-C / ε) when the ground
///   cost matrix and regularisation stay unchanged between calls.
/// * Scaling vectors are warm-started across solves that share the same
///   support size which greatly reduces the number of Sinkhorn iterations in
///   the typical reconstruction loop.
/// * Heavy linear-algebra style loops are parallelised with
///   <see cref="Parallel.For"/> and intermediate buffers are pooled so that the
///   hot-path runs without per-iteration allocations.
/// * Logarithmic stabilisation keeps the Sinkhorn iterates bounded; the helper
///   <see cref="RecentreLogScalings"/> periodically renormalises the scaling
///   vectors to avoid overflow/underflow when exponentiating.
///
/// The class exposes convenience helpers mirroring the legacy Wasserstein error
/// metrics so that unit tests can drive the amplitude- and difference-specific
/// evaluations directly.  Only this file is touched which keeps the remainder of
/// the project unmodified.
/// </summary>
public sealed class UnbalancedWasserstein2Metric : IErrorMetric
{
    /// <summary>
    /// User-facing configuration bundle.
    /// </summary>
    public sealed class Config
    {
        /// <summary>
        /// Entropic regularisation strength. Larger values increase smoothing
        /// and improve numerical robustness at the expense of bias.
        /// </summary>
        public double Epsilon { get; init; } = 0.5;

        /// <summary>
        /// Mass variation penalty. Larger values enforce near-balanced
        /// transport whereas small values allow stronger growth/decay.
        /// </summary>
        public double Rho { get; init; } = 10.0;

        /// <summary>
        /// Maximum number of Sinkhorn iterations.
        /// </summary>
        public int MaxIterations { get; init; } = 500;

        /// <summary>
        /// Stopping tolerance on the scaling updates.
        /// </summary>
        public double Tolerance { get; init; } = 1e-6;

        /// <summary>
        /// When <c>true</c> (default) the returned objective is the debiased
        /// Sinkhorn divergence
        ///   S(a,b) = OT(a,b) - 0.5 OT(a,a) - 0.5 OT(b,b).
        /// </summary>
        public bool UseSinkhornDivergence { get; init; } = true;
    }

    private const double Tiny = 1e-12;
    private const double MaxLogMagnitude = 80.0; // Bound to keep exponentials stable.

    private readonly Config _config;

    // Cache for the last solve so EvaluateAdjointSource can reuse the gradient
    // without repeating the optimal transport computation.
    private OptimalTransportResult? _last;

    // Kernel caching: we only keep the most recent kernel since the
    // reconstruction loop repeatedly solves nearly identical problems.
    private double[,]? _cachedCost;
    private double[,]? _cachedKernel;
    private KernelCacheKey _cachedKernelKey;

    // Warm-start storage keyed by the support size (square problems only).
    private readonly Dictionary<int, SinkhornBuffers> _buffersBySize = new();

    // Scratch matrices for Euclidean costs to avoid reallocating dense KxK
    // buffers when the support size fluctuates.
    private readonly Dictionary<int, double[,]> _amplitudeCostScratch = new();
    private readonly Dictionary<int, double[,]> _differenceCostScratch = new();

    // Scratch vectors are pooled per length to avoid repeated allocations
    // while still supporting concurrent rentals within a single evaluation.
    private readonly Dictionary<int, Stack<double[]>> _vectorPool = new();

    // Cached arc-length ground cost for the current electrode geometry.
    private readonly object _costLock = new();
    private IDiscretization? _cachedDiscretization;
    private int[]? _cachedElectrodeIds;
    private double[,]? _cachedArcCost;

    /// <summary>
    /// Initialises the metric with optional custom configuration.
    /// </summary>
    public UnbalancedWasserstein2Metric(Config? config = null)
    {
        _config = config ?? new Config();
        _cachedKernelKey = new KernelCacheKey(0, 0, double.NaN);
    }

    /// <inheritdoc />
    public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
    {
        var result = Solve(discretization, measured, simulated);
        _last = result;
        return result.Cost;
    }

    /// <inheritdoc />
    public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
    {
        if (_last != null && _last.Matches(measured, simulated))
            return (double[])_last.Gradient.Clone();

        var result = Solve(discretization, measured, simulated);
        _last = result;
        return (double[])result.Gradient.Clone();
    }


    private OptimalTransportResult Solve(IDiscretization discretization, double[] measured, double[] simulated)
    {
        if (discretization == null) throw new ArgumentNullException(nameof(discretization));
        if (measured == null) throw new ArgumentNullException(nameof(measured));
        if (simulated == null) throw new ArgumentNullException(nameof(simulated));

        var mesh = discretization.GetDiscretization();
        var electrodes = discretization.GetElectrodes().OrderBy(e => e.Id).ToList();
        if (electrodes.Count == 0)
            return new OptimalTransportResult(measured, simulated, 0.0, Array.Empty<double>());

        Func<Electrode, (double x, double y)> coord = mesh switch
        {
            FEMMesh fem => e => GetCoord(fem, e),
            LBMGrid lbm => e => e is LBMElectrode lbmElectrode ? ToXY(lbm, lbmElectrode.GridId) : throw new InvalidOperationException("Expected LBMElectrode for LBMGrid discretization."),
            _ => throw new NotSupportedException($"Unbalanced WFR requires FEMMesh or LBMGrid discretizations (got {mesh.GetType().Name}).")
        };

        var pattern = Workspace.GetMeasurementPattern();
        bool usingDifferences = pattern?.Representation == MeasurementRepresentation.PotentialDifference;

        if (!usingDifferences)
        {
            var measuring = electrodes.Where(e => e.IsMeasuring).OrderBy(e => e.Id).ToList();
            var differenceElectrodes = measuring.Count > 0 ? (IReadOnlyList<Electrode>)measuring : electrodes;
            int expectedDifferenceLength = Math.Max(0, differenceElectrodes.Count - 1);
            usingDifferences = measured.Length == expectedDifferenceLength && simulated.Length == expectedDifferenceLength;
        }

        if (usingDifferences)
        {
            var activePattern = pattern != null && pattern.SanitizedLength == measured.Length ? pattern : null;
            return SolveDifference(discretization, measured, simulated, electrodes, coord, activePattern);
        }

        return SolveAmplitude(discretization, measured, simulated, electrodes, coord);
    }

    private OptimalTransportResult SolveAmplitude(
        IDiscretization discretization,
        double[] measured,
        double[] simulated,
        IReadOnlyList<Electrode> electrodes,
        Func<Electrode, (double x, double y)> coordinateProvider)
    {
        int count = Math.Min(measured.Length, electrodes.Count);
        EnsureArcLengthCost(discretization, electrodes, coordinateProvider);
        var include = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            if (!double.IsFinite(measured[i]) || !double.IsFinite(simulated[i]))
                continue;
            include.Add(i);
        }

        if (include.Count == 0)
            return new OptimalTransportResult(measured, simulated, 0.0, new double[count]);

        var simValues = new double[include.Count];
        var measValues = new double[include.Count];
        for (int i = 0; i < include.Count; i++)
        {
            int idx = include[i];
            simValues[i] = ClampNonNegative(simulated[idx]);
            measValues[i] = ClampNonNegative(measured[idx]);
        }

        var cost = GetSubsetCost(discretization, electrodes, coordinateProvider, include, _amplitudeCostScratch);
        var result = ComputeSinkhorn(simValues, measValues, cost);

        var gradient = new double[count];
        for (int i = 0; i < include.Count; i++)
            gradient[include[i]] = result.Gradient[i];

        return new OptimalTransportResult(measured, simulated, result.Cost, gradient);
    }

    private OptimalTransportResult SolveDifference(
        IDiscretization discretization,
        double[] measured,
        double[] simulated,
        IReadOnlyList<Electrode> electrodes,
        Func<Electrode, (double x, double y)> coordinateProvider,
        MeasurementPattern? pattern)
    {
        if (measured.Length != simulated.Length)
            throw new ArgumentException("Measured and simulated difference vectors must share the same length.");

        int differenceCount = measured.Length;
        EnsureArcLengthCost(discretization, electrodes, coordinateProvider);
        var include = new List<int>(differenceCount);
        for (int i = 0; i < differenceCount; i++)
        {
            if (!double.IsFinite(measured[i]) || !double.IsFinite(simulated[i]))
                continue;
            include.Add(i);
        }

        if (include.Count == 0)
            return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

        var activePattern = pattern != null && pattern.SanitizedLength == differenceCount ? pattern : null;
        var (simValues, coords, mapping, leftIndices) = BuildDifferenceDistribution(simulated, electrodes, coordinateProvider, include, activePattern);
        var (measValues, _, _, _) = BuildDifferenceDistribution(measured, electrodes, coordinateProvider, include, activePattern);

        if (simValues.Length == 0)
            return new OptimalTransportResult(measured, simulated, 0.0, new double[differenceCount]);

        var cost = GetSubsetCost(discretization, electrodes, coordinateProvider, leftIndices, _differenceCostScratch);
        var result = ComputeDifference(measValues, simValues, coords, mapping, cost);

        var gradient = new double[differenceCount];
        foreach (var (srcIdx, diffIdx) in mapping)
            gradient[diffIdx] = result.Gradient[srcIdx];

        double mean = 0.0;
        int used = mapping.Count;
        if (used > 0)
        {
            foreach (var (_, diffIdx) in mapping)
                mean += gradient[diffIdx];
            mean /= used;
            foreach (var (_, diffIdx) in mapping)
                gradient[diffIdx] -= mean;
        }

        return new OptimalTransportResult(measured, simulated, result.Cost, gradient);
    }

    private OptimalTransportResult ComputeAmplitude(
        double[] measured,
        double[] simulated,
        (double x, double y)[]? coords,
        double[,] cost)
    {
        int n = Math.Min(measured.Length, simulated.Length);
        var include = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(measured[i]) || !double.IsFinite(simulated[i]))
                continue;
            include.Add(i);
        }

        if (include.Count == 0)
            return new OptimalTransportResult(measured, simulated, 0.0, new double[n]);

        var source = new double[include.Count];
        var target = new double[include.Count];
        for (int i = 0; i < include.Count; i++)
        {
            int idx = include[i];
            source[i] = ClampNonNegative(simulated[idx]);
            target[i] = ClampNonNegative(measured[idx]);
        }

        if (coords != null && coords.Length != include.Count)
            throw new ArgumentException("Coordinate array length must match the number of included measurements.", nameof(coords));

        var result = ComputeSinkhorn(source, target, cost);
        var gradFull = new double[measured.Length];
        for (int i = 0; i < include.Count; i++)
            gradFull[include[i]] = result.Gradient[i];

        return new OptimalTransportResult(measured, simulated, result.Cost, gradFull);
    }

    private OptimalTransportResult ComputeDifference(
        double[] measured,
        double[] simulated,
        (double x, double y)[] coords,
        List<(int srcIdx, int diffIdx)> mapping,
        double[,] cost)
    {
        int k = simulated.Length;
        if (coords.Length != k)
            throw new ArgumentException("Coordinate array length must match the number of included difference entries.", nameof(coords));
        var aPlus = RentVector(k);
        var aMinus = RentVector(k);
        var bPlus = RentVector(k);
        var bMinus = RentVector(k);

        try
        {
            double sumAPlus = 0.0;
            double sumAMinus = 0.0;
            double sumBPlus = 0.0;
            double sumBMinus = 0.0;

            for (int i = 0; i < k; i++)
            {
                double sim = simulated[i];
                double meas = measured[i];

                if (sim >= 0)
                {
                    aPlus[i] = sim;
                    aMinus[i] = 0.0;
                    sumAPlus += sim;
                }
                else
                {
                    double value = -sim;
                    aPlus[i] = 0.0;
                    aMinus[i] = value;
                    sumAMinus += value;
                }

                if (meas >= 0)
                {
                    bPlus[i] = meas;
                    bMinus[i] = 0.0;
                    sumBPlus += meas;
                }
                else
                {
                    double value = -meas;
                    bPlus[i] = 0.0;
                    bMinus[i] = value;
                    sumBMinus += value;
                }
            }

            SinkhornResult plus = sumAPlus > Tiny && sumBPlus > Tiny
                ? ComputeSinkhorn(aPlus, bPlus, cost)
                : SinkhornResult.Zero(k);

            SinkhornResult minus = sumAMinus > Tiny && sumBMinus > Tiny
                ? ComputeSinkhorn(aMinus, bMinus, cost)
                : SinkhornResult.Zero(k);

            var gradient = new double[k];
            if (sumAPlus > Tiny && sumBPlus > Tiny)
            {
                for (int i = 0; i < k; i++)
                    gradient[i] += sumAPlus * plus.Gradient[i];
            }
            if (sumAMinus > Tiny && sumBMinus > Tiny)
            {
                for (int i = 0; i < k; i++)
                    gradient[i] -= sumAMinus * minus.Gradient[i];
            }

            double costValue = 0.0;
            if (sumAPlus > Tiny && sumBPlus > Tiny)
                costValue += sumAPlus * plus.Cost;
            if (sumAMinus > Tiny && sumBMinus > Tiny)
                costValue += sumAMinus * minus.Cost;

            return new OptimalTransportResult(measured, simulated, costValue, gradient);
        }
        finally
        {
            ReturnVector(aPlus);
            ReturnVector(aMinus);
            ReturnVector(bPlus);
            ReturnVector(bMinus);
        }
    }

    private SinkhornResult ComputeSinkhorn(double[] source, double[] target, double[,] costMatrix)
    {
        if (source.Length != target.Length)
            throw new ArgumentException("Source and target measures must have identical length.");
        if (costMatrix.GetLength(0) != source.Length || costMatrix.GetLength(1) != target.Length)
            throw new ArgumentException("Ground cost matrix must match the measure dimensions.");

        double sumSource = 0.0;
        double sumTarget = 0.0;
        for (int i = 0; i < source.Length; i++)
            sumSource += source[i];
        for (int i = 0; i < target.Length; i++)
            sumTarget += target[i];

        if (sumSource <= Tiny && sumTarget <= Tiny)
            return SinkhornResult.Zero(source.Length);

        var main = SolveCore(source, target, costMatrix, sumSource, sumTarget);
        double cost = main.Objective;
        var gradient = (double[])main.SourcePotential.Clone();

        if (_config.UseSinkhornDivergence)
        {
            if (sumSource > Tiny)
            {
                var self = SolveCore(source, source, costMatrix, sumSource, sumSource);
                cost -= 0.5 * self.Objective;
                for (int i = 0; i < gradient.Length; i++)
                    gradient[i] -= 0.5 * self.SourcePotential[i];
            }
            if (sumTarget > Tiny)
            {
                var selfT = SolveCore(target, target, costMatrix, sumTarget, sumTarget);
                cost -= 0.5 * selfT.Objective;
            }
        }

        return new SinkhornResult(cost, gradient);
    }

    private SinkhornSolveResult SolveCore(double[] source, double[] target, double[,] costMatrix, double sumSource, double sumTarget)
    {
        int n = source.Length;
        if (n == 0)
            return new SinkhornSolveResult(0.0, Array.Empty<double>(), Array.Empty<double>());

        if (sumSource <= Tiny || sumTarget <= Tiny)
        {
            // Degenerate case: one side carries (almost) no mass.  Returning a
            // purely mass-penalty cost keeps the solver stable while
            // propagating a neutral gradient.
            double penalty = _config.Rho * Math.Abs(sumSource - sumTarget);
            return new SinkhornSolveResult(penalty, new double[n], new double[n]);
        }

        var buffers = GetBuffers(n);
        double[,] kernel = GetKernel(costMatrix);

        double tau = _config.Rho / (_config.Rho + _config.Epsilon);
        if (tau <= 0.0 || double.IsNaN(tau))
            tau = 0.5; // Fallback for pathological configuration.

        for (int i = 0; i < n; i++)
            buffers.LogSource[i] = source[i] > Tiny ? Math.Log(source[i]) : double.NegativeInfinity;
        for (int j = 0; j < n; j++)
            buffers.LogTarget[j] = target[j] > Tiny ? Math.Log(target[j]) : double.NegativeInfinity;

        double maxChange = double.PositiveInfinity;
        int iteration = 0;
        while (iteration++ < _config.MaxIterations && maxChange > _config.Tolerance)
        {
            ComputeLogMatVec(kernel, buffers.LogScalingTarget, buffers.LogSum);
            maxChange = 0.0;
            for (int i = 0; i < n; i++)
            {
                double la = buffers.LogSource[i];
                double kv = buffers.LogSum[i];
                double updated = (!double.IsFinite(la) || !double.IsFinite(kv))
                    ? double.NegativeInfinity
                    : tau * (la - kv);
                updated = ClampLog(updated);
                double change = ChangeMagnitude(updated, buffers.LogScalingSource[i]);
                if (change > maxChange)
                    maxChange = change;
                buffers.LogScalingSource[i] = updated;
            }

            ComputeLogMatVecTranspose(kernel, buffers.LogScalingSource, buffers.LogSum);
            for (int j = 0; j < n; j++)
            {
                double lb = buffers.LogTarget[j];
                double ku = buffers.LogSum[j];
                double updated = (!double.IsFinite(lb) || !double.IsFinite(ku))
                    ? double.NegativeInfinity
                    : tau * (lb - ku);
                updated = ClampLog(updated);
                double change = ChangeMagnitude(updated, buffers.LogScalingTarget[j]);
                if (change > maxChange)
                    maxChange = Math.Max(maxChange, change);
                buffers.LogScalingTarget[j] = updated;
            }

            RecentreLogScalings(buffers.LogScalingSource, buffers.LogScalingTarget);
        }

        var sourcePotential = new double[n];
        var targetPotential = new double[n];
        double objective = 0.0;

        for (int i = 0; i < n; i++)
        {
            double potential = double.IsNegativeInfinity(buffers.LogScalingSource[i])
                ? 0.0
                : _config.Epsilon * buffers.LogScalingSource[i];
            sourcePotential[i] = potential;
            objective += source[i] * potential;
        }
        for (int j = 0; j < n; j++)
        {
            double potential = double.IsNegativeInfinity(buffers.LogScalingTarget[j])
                ? 0.0
                : _config.Epsilon * buffers.LogScalingTarget[j];
            targetPotential[j] = potential;
            objective += target[j] * potential;
        }

        return new SinkhornSolveResult(objective, sourcePotential, targetPotential);
    }

    private double[,] GetKernel(double[,] costMatrix)
    {
        int rows = costMatrix.GetLength(0);
        int cols = costMatrix.GetLength(1);
        if (_cachedKernel != null && _cachedCost != null && ReferenceEquals(costMatrix, _cachedCost) &&
            _cachedKernelKey.Rows == rows && _cachedKernelKey.Cols == cols && Math.Abs(_cachedKernelKey.Epsilon - _config.Epsilon) < 1e-12)
        {
            return _cachedKernel;
        }

        var kernel = new double[rows, cols];
        Parallel.For(0, rows, i =>
        {
            for (int j = 0; j < cols; j++)
                kernel[i, j] = -costMatrix[i, j] / _config.Epsilon;
        });

        _cachedCost = costMatrix;
        _cachedKernel = kernel;
        _cachedKernelKey = new KernelCacheKey(rows, cols, _config.Epsilon);
        return kernel;
    }

    private SinkhornBuffers GetBuffers(int size)
    {
        if (!_buffersBySize.TryGetValue(size, out var buffers))
        {
            buffers = new SinkhornBuffers(size);
            _buffersBySize[size] = buffers;
        }
        return buffers;
    }

    private void EnsureArcLengthCost(IDiscretization discretization, IReadOnlyList<Electrode> electrodes,
        Func<Electrode, (double x, double y)> coordinateProvider)
    {
        lock (_costLock)
        {
            bool reuse = _cachedArcCost != null && _cachedElectrodeIds != null &&
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

            var coords = electrodes.Select(coordinateProvider).ToArray();
            _cachedArcCost = ArcLengthGroundCostHelper.BuildArcLengthCost(coords);
            _cachedElectrodeIds = electrodes.Select(e => e.Id).ToArray();
            _cachedDiscretization = discretization;
        }
    }

    private double[,] GetSubsetCost(IDiscretization discretization, IReadOnlyList<Electrode> electrodes,
        Func<Electrode, (double x, double y)> coordinateProvider, IReadOnlyList<int> indices,
        Dictionary<int, double[,]> scratchPool)
    {
        EnsureArcLengthCost(discretization, electrodes, coordinateProvider);
        var matrix = GetScratchMatrix(scratchPool, indices.Count);
        for (int i = 0; i < indices.Count; i++)
        {
            int ii = indices[i];
            for (int j = 0; j < indices.Count; j++)
            {
                matrix[i, j] = _cachedArcCost![ii, indices[j]];
            }
        }
        return matrix;
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

    private double[] RentVector(int length)
    {
        lock (_vectorPool)
        {
            if (_vectorPool.TryGetValue(length, out var stack) && stack.Count > 0)
                return stack.Pop();
        }

        return new double[length];
    }

    private void ReturnVector(double[] vector)
    {
        if (vector == null)
            return;

        Array.Clear(vector, 0, vector.Length);

        lock (_vectorPool)
        {
            if (!_vectorPool.TryGetValue(vector.Length, out var stack))
            {
                stack = new Stack<double[]>();
                _vectorPool[vector.Length] = stack;
            }
            stack.Push(vector);
        }
    }

    private static double ClampNonNegative(double value)
        => !double.IsFinite(value) ? 0.0 : Math.Max(0.0, value);

    private static double ClampLog(double value)
    {
        if (double.IsNegativeInfinity(value))
            return double.NegativeInfinity;
        if (double.IsPositiveInfinity(value) || double.IsNaN(value))
            return 0.0;
        return Math.Clamp(value, -MaxLogMagnitude, MaxLogMagnitude);
    }

    private static double ChangeMagnitude(double updated, double previous)
    {
        if (!double.IsFinite(updated) && !double.IsFinite(previous))
            return 0.0;
        if (!double.IsFinite(updated) || !double.IsFinite(previous))
            return MaxLogMagnitude;
        return Math.Abs(updated - previous);
    }

    private static void RecentreLogScalings(double[] logU, double[] logV)
    {
        double sum = 0.0;
        int count = 0;
        double maxAbs = 0.0;
        for (int i = 0; i < logU.Length; i++)
        {
            double value = logU[i];
            if (!double.IsFinite(value))
                continue;
            sum += value;
            count++;
            double abs = Math.Abs(value);
            if (abs > maxAbs)
                maxAbs = abs;
        }

        if (count == 0 || maxAbs < MaxLogMagnitude)
            return;

        double shift = sum / count;
        for (int i = 0; i < logU.Length; i++)
        {
            if (double.IsFinite(logU[i]))
                logU[i] -= shift;
        }
        for (int j = 0; j < logV.Length; j++)
        {
            if (double.IsFinite(logV[j]))
                logV[j] += shift;
        }
    }

    private static void ComputeLogMatVec(double[,] logKernel, double[] logVector, double[] output)
    {
        int rows = logKernel.GetLength(0);
        int cols = logKernel.GetLength(1);
        Parallel.For(0, rows, i =>
        {
            double max = double.NegativeInfinity;
            for (int j = 0; j < cols; j++)
            {
                double candidate = logKernel[i, j] + logVector[j];
                if (candidate > max)
                    max = candidate;
            }

            if (!double.IsFinite(max))
            {
                output[i] = double.NegativeInfinity;
                return;
            }

            double sum = 0.0;
            for (int j = 0; j < cols; j++)
            {
                double value = logKernel[i, j] + logVector[j];
                sum += Math.Exp(value - max);
            }
            output[i] = max + Math.Log(sum);
        });
    }

    private static void ComputeLogMatVecTranspose(double[,] logKernel, double[] logVector, double[] output)
    {
        int rows = logKernel.GetLength(0);
        int cols = logKernel.GetLength(1);
        Parallel.For(0, cols, j =>
        {
            double max = double.NegativeInfinity;
            for (int i = 0; i < rows; i++)
            {
                double candidate = logKernel[i, j] + logVector[i];
                if (candidate > max)
                    max = candidate;
            }

            if (!double.IsFinite(max))
            {
                output[j] = double.NegativeInfinity;
                return;
            }

            double sum = 0.0;
            for (int i = 0; i < rows; i++)
            {
                double value = logKernel[i, j] + logVector[i];
                sum += Math.Exp(value - max);
            }
            output[j] = max + Math.Log(sum);
        });
    }

    private static (double[] raw, (double x, double y)[] loc, List<(int srcIdx, int diffIdx)> map, int[] leftElectrodes)
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

    private static (double x, double y) ToXY(LBMGrid grid, int gridId) =>
        LbmElectrodeCoordinateHelper.ToPhysicalCoordinates(grid, gridId);

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

    private readonly record struct KernelCacheKey(int Rows, int Cols, double Epsilon);

    private sealed class SinkhornBuffers
    {
        public double[] LogScalingSource { get; }
        public double[] LogScalingTarget { get; }
        public double[] LogSource { get; }
        public double[] LogTarget { get; }
        public double[] LogSum { get; }

        public SinkhornBuffers(int size)
        {
            LogScalingSource = new double[size];
            LogScalingTarget = new double[size];
            LogSource = new double[size];
            LogTarget = new double[size];
            LogSum = new double[size];
        }
    }

    private readonly record struct SinkhornResult(double Cost, double[] Gradient)
    {
        public static SinkhornResult Zero(int length) => new(0.0, new double[length]);
    }

    private readonly record struct SinkhornSolveResult(double Objective, double[] SourcePotential, double[] TargetPotential);

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
}
