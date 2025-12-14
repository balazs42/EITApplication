using System.Reflection;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Reconstruction.ErrorMetrics;
using Xunit;

namespace Utility.Tests;

public sealed class ConductivityAwareW2MetricTests
{
    [Fact]
    public void CostMatrixOverloadMatchesCoordinateVersion()
    {
        double[] a = { 0.2, 0.5, 0.3 };
        double[] b = { 0.4, 0.1, 0.5 };
        var coords = new (double x, double y)[]
        {
            (0.0, 0.0),
            (1.0, 0.0),
            (0.0, 1.0)
        };

        var baseline = ConductivityAwareW2Metric.w2_misfit_and_grad(a, b, coords, coords);

        double[,] cost =
        {
            { 0.0, 1.0, 1.0 },
            { 1.0, 0.0, 2.0 },
            { 1.0, 2.0, 0.0 }
        };

        var costMatrix = ConductivityAwareW2Metric.w2_misfit_and_grad(a, b, cost);

        Assert.Equal(baseline.Cost, costMatrix.Cost, 6);
        Assert.Equal(baseline.Grad, costMatrix.Grad);
    }

    [Fact]
    public void WarmupDelaysConductivityActivation()
    {
        var grid = new LBMGrid(5, 5);
        grid.PlaceEquidistantElectrodes(4);

        double[] measured = { 0.3, 0.4, 0.2, 0.1 };
        double[] simulated = { 0.35, 0.2, 0.25, 0.2 };

        var config = new ConductivityAwareW2Metric.Config
        {
            WarmupSolves = 2,
            TargetAlpha = 1.0,
            RecomputeEvery = 1,
            SigmaChangeTolerance = 0.0,
        };

        var metric = new ConductivityAwareW2Metric(config);

        _ = metric.Evaluate(grid, measured, simulated);
        bool afterFirst = GetPrivate<bool>(metric, "_useConductivityAware");
        Assert.False(afterFirst);

        _ = metric.Evaluate(grid, measured, simulated);
        bool afterSecond = GetPrivate<bool>(metric, "_useConductivityAware");
        Assert.True(afterSecond);
    }

    [Fact]
    public void GroundCostRebuildCreatesSymmetricMatrices()
    {
        var grid = new LBMGrid(6, 6);
        grid.PlaceEquidistantElectrodes(4);

        double[] measured = { 0.2, 0.5, 0.1, 0.2 };
        double[] simulated = { 0.3, 0.1, 0.4, 0.2 };

        var metric = new ConductivityAwareW2Metric(new ConductivityAwareW2Metric.Config
        {
            WarmupSolves = 0,
            TargetAlpha = 0.8,
            RecomputeEvery = 1,
            SigmaChangeTolerance = 0.0,
        });

        _ = metric.Evaluate(grid, measured, simulated);

        var cAmp = GetPrivate<double[,]?>(metric, "_cAmp");
        Assert.NotNull(cAmp);

        int n = cAmp!.GetLength(0);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(0.0, cAmp[i, i], 6);
            for (int j = i + 1; j < n; j++)
            {
                Assert.Equal(cAmp[i, j], cAmp[j, i], 6);
                Assert.True(cAmp[i, j] >= 0.0);
            }
        }

        double alpha = GetPrivate<double>(metric, "_alphaCurrent");
        Assert.InRange(alpha, 0.0, 1.0);
    }

    private static T GetPrivate<T>(object instance, string field)
    {
        var info = instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
                   ?? throw new InvalidOperationException($"Field '{field}' not found on {instance.GetType().Name}.");
        return (T)info.GetValue(instance)!;
    }
}
