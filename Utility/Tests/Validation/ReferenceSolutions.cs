namespace Utility.Tests.Validation;

/// <summary>
/// Analytic solutions used to validate numerical EIT solvers.
/// </summary>
public static class ReferenceSolutions
{
    /// <summary>
    /// Analytic potential inside the unit disc for a homogeneous conductivity
    /// driven by a Fourier boundary current
    /// <c>g(θ)=∑ₙ αₙ cos(nθ)+βₙ sin(nθ)</c>.
    /// The resulting potential is
    /// <c>u(r,θ) = (1/σ₀) ∑ₙ rⁿ/n (αₙ cos(nθ)+βₙ sin(nθ))</c>.
    /// </summary>
    public static double ReferencePotential_FourierSeries(double sigma0, double[] alphas, double[] betas, double r, double theta)
    {
        if (alphas.Length != betas.Length)
            throw new ArgumentException("alpha/beta length mismatch");
        double sum = 0.0;
        for (int n = 1; n <= alphas.Length; n++)
        {
            double cosTerm = Math.Cos(n * theta);
            double sinTerm = Math.Sin(n * theta);
            // accumulate r^n/n * (α_n cos nθ + β_n sin nθ)
            sum += Math.Pow(r, n) * (alphas[n - 1] * cosTerm + betas[n - 1] * sinTerm) / n;
        }
        // Divide by σ₀ as per analytic solution
        return sum / sigma0;
    }

    /// <summary>
    /// Potential of a dipole source in an infinite plane restricted to the
    /// circle, computed from
    /// <c>u(x)=I/(2πσ₀) log(|x-a|/|x-b|)</c>.
    /// </summary>
    public static double ReferencePotential_Dipole(double sigma0, double current, (double X, double Y) a, (double X, double Y) b, (double X, double Y) x)
    {
        double distA = Math.Sqrt((x.X - a.X) * (x.X - a.X) + (x.Y - a.Y) * (x.Y - a.Y));
        double distB = Math.Sqrt((x.X - b.X) * (x.X - b.X) + (x.Y - b.Y) * (x.Y - b.Y));
        return (current / (2 * Math.PI * sigma0)) * Math.Log(distA / distB);
    }

    /// <summary>
    /// Reference potential for concentric layers with piecewise constant
    /// conductivity. Selects the conductivity of the layer containing
    /// <paramref name="r"/> and evaluates the Fourier series solution
    /// for that σ.
    /// </summary>
    public static double ReferencePotential_LayeredCircle(double[] sigmas, double[] radii, double[] alphas, double[] betas, double r, double theta)
    {
        if (sigmas.Length != radii.Length)
            throw new ArgumentException("sigmas and radii must match");
        int layer = 0;
        while (layer < radii.Length && r > radii[layer])
            layer++;
        double sigma = sigmas[Math.Min(layer, sigmas.Length - 1)];
        return ReferencePotential_FourierSeries(sigma, alphas, betas, r, theta);
    }

    /// <summary>
    /// First order polarization response of a small circular inclusion
    /// with radius <paramref name="radius"/> and contrast σ₁/σ₀.
    /// Derived from the tensor <c>M = 2π a² (σ₁-σ₀)/(σ₁+σ₀)</c>.
    /// </summary>
    public static double ReferenceResponse_SmallInclusion(double sigma0, double sigma1, double radius)
    {
        return 2 * Math.PI * radius * radius * (sigma1 - sigma0) / (sigma1 + sigma0);
    }

    /// <summary>
    /// Gradient of the Fourier series potential obtained by differentiating
    /// the analytic expression in polar coordinates and mapping to Cartesian
    /// components.
    /// </summary>
    public static (double Gx, double Gy) ReferenceGradient_FourierSeries(double sigma0, double[] alphas, double[] betas, double r, double theta)
    {
        if (alphas.Length != betas.Length)
            throw new ArgumentException("alpha/beta length mismatch");

        double dr = 0.0;      // ∂u/∂r
        double dtheta = 0.0;  // ∂u/∂θ
        for (int n = 1; n <= alphas.Length; n++)
        {
            double cosTerm = Math.Cos(n * theta);
            double sinTerm = Math.Sin(n * theta);
            dr += Math.Pow(r, n - 1) * (alphas[n - 1] * cosTerm + betas[n - 1] * sinTerm);
            dtheta += Math.Pow(r, n) * (-alphas[n - 1] * sinTerm + betas[n - 1] * cosTerm);
        }
        dr /= sigma0;
        dtheta /= sigma0;

        // Convert (∂u/∂r, ∂u/∂θ) to Cartesian (∂u/∂x, ∂u/∂y)
        double gx = dr * Math.Cos(theta) - (dtheta / r) * Math.Sin(theta);
        double gy = dr * Math.Sin(theta) + (dtheta / r) * Math.Cos(theta);
        return (gx, gy);
    }
}
