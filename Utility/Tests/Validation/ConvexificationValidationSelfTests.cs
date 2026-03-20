using Utility.Classes.Reconstruction.Convexification;

namespace Utility.Tests.Validation;

/// <summary>
/// Lightweight validation checks for the convexification-specific helper logic.
/// These focus on the raw electrode transforms that can be tested without
/// bootstrapping the full service stack.
/// </summary>
public static class ConvexificationValidationSelfTests
{
    /// <summary>
    /// Confirms that the cyclic finite-difference helper applies the periodic
    /// central difference formula over a complete drive cycle.
    /// </summary>
    public static void TestPeriodicDerivativeConsistency()
    {
        var samples = new List<double[]>
        {
            new[] { 0.0, 5.0 },
            new[] { 1.0, 5.0 },
            new[] { 0.0, 5.0 },
            new[] { -1.0, 5.0 }
        };

        var derivatives = ConvexificationOperators.ComputeDriveDerivatives(samples,
                                                                           new[] { 0, 1, 2, 3 },
                                                                           cycleLength: 4,
                                                                           usePeriodicWhenAvailable: true);

        double[][] expected =
        [
            new[] { 1.0, 0.0 },
            new[] { 0.0, 0.0 },
            new[] { -1.0, 0.0 },
            new[] { 0.0, 0.0 }
        ];

        for (int frameIndex = 0; frameIndex < expected.Length; frameIndex++)
        {
            for (int electrodeIndex = 0; electrodeIndex < expected[frameIndex].Length; electrodeIndex++)
            {
                double actual = derivatives[frameIndex][electrodeIndex];
                if (Math.Abs(actual - expected[frameIndex][electrodeIndex]) > 1e-12)
                {
                    throw new Exception($"Periodic derivative mismatch at frame {frameIndex}, electrode {electrodeIndex}: expected {expected[frameIndex][electrodeIndex]}, got {actual}.");
                }
            }
        }
    }

    /// <summary>
    /// Verifies that the practical positivity-shift rule keeps every g0 entry
    /// strictly positive before the logarithmic transform is applied.
    /// </summary>
    public static void TestPositivityShiftSafety()
    {
        double[] voltages = { -0.35, -0.20, 0.05, -0.10 };
        double[] currents = { 1.0, 0.0, -1.0, 0.0 };
        double[] lengths = { 1.0, 1.0, 1.0, 1.0 };
        double[] impedances = { 0.10, 0.10, 0.10, 0.10 };

        double[] rawProxy = new double[voltages.Length];
        for (int i = 0; i < rawProxy.Length; i++)
            rawProxy[i] = voltages[i] - impedances[i] * currents[i] / lengths[i];

        double d0 = 0.25;
        double margin = 1e-3;
        double shift = ConvexificationOperators.ComputePositivityShift(rawProxy.Min(), d0, margin);

        foreach (double value in rawProxy.Select(v => v + shift))
        {
            if (!double.IsFinite(value) || value <= 0.0)
                throw new Exception("Positivity-shift rule failed to produce strictly positive g0 values.");
        }
    }

    /// <summary>
    /// Verifies that optional periodic smoothing leaves a constant electrode
    /// signal unchanged before differentiation.
    /// </summary>
    public static void TestDerivativeSmoothingPreservesConstantSignal()
    {
        var samples = new List<double[]>
        {
            new[] { 2.0, -1.0 },
            new[] { 2.0, -1.0 },
            new[] { 2.0, -1.0 },
            new[] { 2.0, -1.0 }
        };

        var smoothed = ConvexificationOperators.SmoothDriveSamples(samples,
                                                                   new[] { 0, 1, 2, 3 },
                                                                   cycleLength: 4,
                                                                   smoothingWindow: 3,
                                                                   smoothingPasses: 2,
                                                                   usePeriodicSmoothing: true);
        var derivatives = ConvexificationOperators.ComputeDriveDerivatives(samples,
                                                                           new[] { 0, 1, 2, 3 },
                                                                           cycleLength: 4,
                                                                           usePeriodicWhenAvailable: true,
                                                                           smoothingWindow: 3,
                                                                           smoothingPasses: 2,
                                                                           usePeriodicSmoothing: true);

        foreach (var frame in smoothed)
        {
            if (Math.Abs(frame[0] - 2.0) > 1e-12 || Math.Abs(frame[1] + 1.0) > 1e-12)
                throw new Exception("Periodic smoothing changed a constant drive signal.");
        }

        foreach (var frame in derivatives)
        {
            if (Math.Abs(frame[0]) > 1e-12 || Math.Abs(frame[1]) > 1e-12)
                throw new Exception("Derivative of a constant smoothed drive signal was not zero.");
        }
    }

    /// <summary>
    /// Verifies that the recovered V floor is kept consistent with the
    /// conductivity floor because sigma = V^2.
    /// </summary>
    public static void TestMinimumScaleRespectsConductivityFloor()
    {
        var options = new ConvexificationOptions
        {
            MinimumScale = 0.05
        };

        double effectiveMinimumScale = ConvexificationOperators.ResolveMinimumScale(options, conductivityMinimumBound: 0.1);
        if (effectiveMinimumScale + 1e-12 < Math.Sqrt(0.1))
            throw new Exception("Resolved minimum scale fell below sqrt(conductivity minimum bound).");
    }

    /// <summary>
    /// Verifies that the line-search acceptance helper tolerates pure roundoff
    /// differences rather than rejecting a numerically identical objective.
    /// </summary>
    public static void TestObjectiveAcceptanceToleranceAllowsRoundoff()
    {
        var options = new ConvexificationOptions
        {
            ObjectiveAcceptanceTolerance = 1e-6
        };

        var result = ConvexificationOperators.EvaluateObjectiveAcceptance(10.0, 10.0 + 5e-7, options);
        if (!result.Accepted)
            throw new Exception("Objective acceptance helper rejected a candidate within the configured tolerance.");
    }

    /// <summary>
    /// Verifies that the practical line-search relative tolerance can accept a
    /// tiny surrogate-model increase without stalling the inner descent.
    /// </summary>
    public static void TestLineSearchRelativeToleranceAllowsStableCandidate()
    {
        var options = new ConvexificationOptions
        {
            ObjectiveAcceptanceTolerance = 0.0,
            LineSearchRelativeTolerance = 5e-5
        };

        var result = ConvexificationOperators.EvaluateObjectiveAcceptance(100.0, 100.003, options);
        if (!result.Accepted)
            throw new Exception("Line-search relative tolerance rejected a numerically stable surrogate candidate.");
    }
}
