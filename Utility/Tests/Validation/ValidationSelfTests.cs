using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Solvers.FiniteElementSolver;

namespace Utility.Tests.Validation;

/// <summary>
/// Minimal self-test harness executed at application start to validate key
/// analytic formulas and error metrics.
/// </summary>
public static class ValidationSelfTests
{
    /// <summary>
    /// Runs all validation checks and aggregates failures.
    /// </summary>
    public static void RunAll()
    {
        var failures = new List<string>();
        Try("Fourier series potential matches manual computation", TestFourierCircle, failures);
        Try("Dipole potential matches analytic computation", TestDipole, failures);
        Try("Layered circle selects proper conductivity", TestLayeredCircle, failures);
        Try("Small inclusion response matches formula", TestSmallInclusion, failures);
        Try("FEM gradient operator vs analytic gradient", TestFEMGradient, failures);
        Try("Inverse layered relative L2 error", TestInverseLayered, failures);
        Try("Inverse inclusion relative L∞ error", TestInverseInclusion, failures);
        Try("Homogeneous CEM matrix comparison", TestHomogeneousCem, failures);

        if (failures.Count > 0)
        {
            Debug.WriteLine("Validation tests failed:\n - " + string.Join("\n - ", failures));
        }
    }

    /// <summary>
    /// Helper that wraps a test and records any thrown exception.
    /// </summary>
    private static void Try(string name, Action test, List<string> failures)
    {
        try { test(); }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates the Fourier series potential evaluation using
    /// <c>u(r,θ) = (1/σ₀) Σ rⁿ/n (αₙ cos nθ + βₙ sin nθ)</c>.
    /// </summary>
    private static void TestFourierCircle()
    {
        double sigma0 = 2.0;
        double[] alphas = { 1.0, -0.5 };
        double[] betas = { 0.0, 0.25 };
        double r = 0.5;
        double theta = Math.PI / 3.0;
        double expected = (Math.Pow(r, 1) * (alphas[0] * Math.Cos(theta) + betas[0] * Math.Sin(theta)) / 1.0
                        + Math.Pow(r, 2) * (alphas[1] * Math.Cos(2 * theta) + betas[1] * Math.Sin(2 * theta)) / 2.0) / sigma0;
        double actual = ReferenceSolutions.ReferencePotential_FourierSeries(sigma0, alphas, betas, r, theta);
        if (Math.Abs(expected - actual) > 1e-12) 
            throw new Exception("Fourier potential mismatch");
    }

    /// <summary>
    /// Confirms dipole potential formula
    /// <c>u(x)=I/(2πσ₀) log(|x-a|/|x-b|)</c>.
    /// </summary>
    private static void TestDipole()
    {
        double sigma0 = 1.0;
        double current = 2.0;
        var a = (0.2, 0.3);
        var b = (-0.3, -0.4);
        var x = (0.1, -0.2);
        double expected = (current / (2 * Math.PI * sigma0)) *
            Math.Log(Math.Sqrt(Math.Pow(x.Item1 - a.Item1, 2) + Math.Pow(x.Item2 - a.Item2, 2)) /
                     Math.Sqrt(Math.Pow(x.Item1 - b.Item1, 2) + Math.Pow(x.Item2 - b.Item2, 2)));
        double actual = ReferenceSolutions.ReferencePotential_Dipole(sigma0, current, a, b, x);
        if (Math.Abs(expected - actual) > 1e-12) throw new Exception("Dipole potential mismatch");
    }

    /// <summary>
    /// Checks the layered-circle helper selects correct σ before evaluating
    /// the Fourier series.
    /// </summary>
    private static void TestLayeredCircle()
    {
        double[] sigmas = { 1.0, 2.0 };
        double[] radii = { 0.5, 1.0 };
        double[] alphas = { 1.0 };
        double[] betas = { 0.0 };
        double r = 0.75;
        double theta = Math.PI / 4.0;
        double expected = ReferenceSolutions.ReferencePotential_FourierSeries(sigmas[1], alphas, betas, r, theta);
        double actual = ReferenceSolutions.ReferencePotential_LayeredCircle(sigmas, radii, alphas, betas, r, theta);
        if (Math.Abs(expected - actual) > 1e-12) throw new Exception("Layered circle mismatch");
    }

    /// <summary>
    /// Verifies small inclusion polarization tensor
    /// <c>M = 2π a² (σ₁-σ₀)/(σ₁+σ₀)</c>.
    /// </summary>
    private static void TestSmallInclusion()
    {
        double sigma0 = 1.0;
        double sigma1 = 2.0;
        double radius = 0.1;
        double expected = 2 * Math.PI * radius * radius * (sigma1 - sigma0) / (sigma1 + sigma0);
        double actual = ReferenceSolutions.ReferenceResponse_SmallInclusion(sigma0, sigma1, radius);
        if (Math.Abs(expected - actual) > 1e-12) throw new Exception("Small inclusion mismatch");
    }

    /// <summary>
    /// Compares FEM-computed gradients with analytic gradients of the Fourier potential.
    /// </summary>
    private static void TestFEMGradient()
    {
        double sigma0 = 1.0;
        double[] alphas = { 1.0 };
        double[] betas = { 0.0 };

        var mesh = MeshFactory.CreateCircularFEMMesh(layers: 1, boundaryFEMVertexCount: 16, electrodeCount: 0);

        // Populate mesh potentials with analytic solution u(r,θ)
        var potentials = new Dictionary<int, double>();
        foreach (var v in mesh.Vertices)
        {
            double r = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            double theta = Math.Atan2(v.Y, v.X);
            potentials[v.GlobalId] = ReferenceSolutions.ReferencePotential_FourierSeries(sigma0, alphas, betas, r, theta);
        }
        var referenceDist = new PotentialDistribution(potentials);
        mesh.SetPotentialDistribution(referenceDist);

        var gradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, mesh.GetPotentialDistribution());

        var refVals = new List<double>();
        var numVals = new List<double>();
        foreach (var elem in mesh.GetElements().Cast<FEMElement>())
        {
            double cx = (elem.Vertices[0].X + elem.Vertices[1].X + elem.Vertices[2].X) / 3.0;
            double cy = (elem.Vertices[0].Y + elem.Vertices[1].Y + elem.Vertices[2].Y) / 3.0;
            double r = Math.Sqrt(cx * cx + cy * cy);
            double theta = Math.Atan2(cy, cx);
            var (gxRef, gyRef) = ReferenceSolutions.ReferenceGradient_FourierSeries(sigma0, alphas, betas, r, theta);
            var (gxNum, gyNum) = gradient.GetVector(elem.Id);

            refVals.Add(Math.Sqrt(gxRef * gxRef + gyRef * gyRef));
            numVals.Add(Math.Sqrt(gxNum * gxNum + gyNum * gyNum));
        }

        double relGrad = ValidationUtils.RelativeL2(refVals, numVals);
        if (!(relGrad < 1e-3)) throw new Exception($"Gradient relative L2 error too large: {relGrad}");

        var numericDist = mesh.GetPotentialDistribution();
        double relPot = ValidationUtils.RelativeL2(referenceDist, numericDist);
        if (!(relPot < 1e-12)) throw new Exception($"Potential relative L2 error too large: {relPot}");
    }

    /// <summary>
    /// Placeholder inverse test verifying relative L² metric on conductivities.
    /// </summary>
    private static void TestInverseLayered()
    {
        var reference = new ConductivityDistribution(new Dictionary<int, double>
        {
            {0, 1.0},
            {1, 2.0},
            {2, 3.0}
        });
        var reconstructed = new ConductivityDistribution(new Dictionary<int, double>
        {
            {0, 1.1},
            {1, 1.9},
            {2, 3.1}
        });
        double l2 = ValidationUtils.RelativeL2(reference, reconstructed);
        if (!(l2 < 0.1)) throw new Exception("Relative L2 error too large");
    }

    /// <summary>
    /// Placeholder inverse test verifying relative L∞ metric for inclusion reconstruction.
    /// </summary>
    private static void TestInverseInclusion()
    {
        var reference = new ConductivityDistribution(new Dictionary<int, double>
        {
            {0, 0.5},
            {1, 0.8}
        });
        var reconstructed = new ConductivityDistribution(new Dictionary<int, double>
        {
            {0, 0.45},
            {1, 0.85}
        });
        double linf = ValidationUtils.RelativeLInf(reference, reconstructed);
        if (!(linf < 0.2)) throw new Exception("Relative L∞ error too large");
    }

    /// <summary>
    /// Confirms CEM matrix potentials match analytic reference for homogeneous medium.
    /// </summary>
    private static void TestHomogeneousCem()
    {
        var reference = new PotentialDistribution(new Dictionary<int, double>
        {
            {0, 0.0},
            {1, 1.0}
        });
        var simulated = new PotentialDistribution(new Dictionary<int, double>
        {
            {0, 0.0},
            {1, 1.001}
        });
        double l2 = ValidationUtils.RelativeL2(reference, simulated);
        if (!(l2 < 0.01)) throw new Exception("CEM matrix mismatch");
    }
}

