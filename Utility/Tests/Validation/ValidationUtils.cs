using Utility.Classes;

namespace Utility.Tests.Validation;

/// <summary>
/// Numeric error metrics used by the validation suite.
/// The relative L² metric is defined as
/// <c>‖v - v_ref‖₂ / ‖v_ref‖₂</c> and the relative L∞ metric as
/// <c>‖v - v_ref‖∞ / ‖v_ref‖∞</c>.
/// </summary>
public static class ValidationUtils
{
    /// <summary>
    /// Relative L² error for plain vectors:
    /// <c>sqrt( Σᵢ (vᵢ - vᵢ^ref)² / Σᵢ (vᵢ^ref)² )</c>.
    /// </summary>
    public static double RelativeL2(IReadOnlyList<double> reference, IReadOnlyList<double> values)
    {
        if (reference.Count != values.Count)
            throw new ArgumentException("Length mismatch");
        double num = 0.0, den = 0.0;
        for (int i = 0; i < reference.Count; i++)
        {
            double diff = values[i] - reference[i];
            num += diff * diff;
            den += reference[i] * reference[i];
        }
        return Math.Sqrt(num / den);
    }

    /// <summary>
    /// Relative L∞ error for plain vectors:
    /// <c>maxᵢ |vᵢ - vᵢ^ref| / maxᵢ |vᵢ^ref|</c>.
    /// </summary>
    public static double RelativeLInf(IReadOnlyList<double> reference, IReadOnlyList<double> values)
    {
        if (reference.Count != values.Count)
            throw new ArgumentException("Length mismatch");
        double num = 0.0, den = 0.0;
        for (int i = 0; i < reference.Count; i++)
        {
            num = Math.Max(num, Math.Abs(values[i] - reference[i]));
            den = Math.Max(den, Math.Abs(reference[i]));
        }
        return num / den;
    }

    /// <summary>
    /// Relative L² error computed directly on <see cref="PotentialDistribution"/> objects.
    /// </summary>
    public static double RelativeL2(PotentialDistribution reference, PotentialDistribution values)
    {
        double num = 0.0, den = 0.0;
        foreach (var kvp in reference.Potentials)
        {
            if (!values.Potentials.TryGetValue(kvp.Key, out double val))
                throw new ArgumentException($"Key {kvp.Key} missing in values");
            double diff = val - kvp.Value;
            num += diff * diff;
            den += kvp.Value * kvp.Value;
        }
        return Math.Sqrt(num / den);
    }

    /// <summary>
    /// Relative L∞ error on <see cref="PotentialDistribution"/> objects.
    /// </summary>
    public static double RelativeLInf(PotentialDistribution reference, PotentialDistribution values)
    {
        double num = 0.0, den = 0.0;
        foreach (var kvp in reference.Potentials)
        {
            if (!values.Potentials.TryGetValue(kvp.Key, out double val))
                throw new ArgumentException($"Key {kvp.Key} missing in values");
            num = Math.Max(num, Math.Abs(val - kvp.Value));
            den = Math.Max(den, Math.Abs(kvp.Value));
        }
        return num / den;
    }

    /// <summary>
    /// Relative L² error for <see cref="ConductivityDistribution"/> objects.
    /// </summary>
    public static double RelativeL2(ConductivityDistribution reference, ConductivityDistribution values)
    {
        double num = 0.0, den = 0.0;
        foreach (var kvp in reference.Conductivities)
        {
            if (!values.Conductivities.TryGetValue(kvp.Key, out double val))
                throw new ArgumentException($"Key {kvp.Key} missing in values");
            double diff = val - kvp.Value;
            num += diff * diff;
            den += kvp.Value * kvp.Value;
        }
        return Math.Sqrt(num / den);
    }

    /// <summary>
    /// Relative L∞ error for <see cref="ConductivityDistribution"/> objects.
    /// </summary>
    public static double RelativeLInf(ConductivityDistribution reference, ConductivityDistribution values)
    {
        double num = 0.0, den = 0.0;
        foreach (var kvp in reference.Conductivities)
        {
            if (!values.Conductivities.TryGetValue(kvp.Key, out double val))
                throw new ArgumentException($"Key {kvp.Key} missing in values");
            num = Math.Max(num, Math.Abs(val - kvp.Value));
            den = Math.Max(den, Math.Abs(kvp.Value));
        }
        return num / den;
    }
}
