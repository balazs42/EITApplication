using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.Factories;
using Utility.Classes.Reconstruction.NumericSolvers;
using Utility.Classes.ReconstructionParameters;
using Xunit;

namespace Utility.Tests;

public class GmresSolverGpuSelectionTests
{
    [Fact]
    public void SelectVectorExecutionMode_UsesCudaForLargeSystemsWhenAvailable()
    {
        var mode = GmresSolver.SelectVectorExecutionMode(
            useCudaAcceleration: true,
            systemDimension: GmresSolver.DefaultCudaVectorThresholdDimension,
            cudaAvailable: true);

        Assert.Equal(GmresVectorExecutionMode.Cuda, mode);
    }

    [Fact]
    public void SelectVectorExecutionMode_FallsBackToCpuWhenCudaIsUnavailable()
    {
        var mode = GmresSolver.SelectVectorExecutionMode(
            useCudaAcceleration: true,
            systemDimension: GmresSolver.DefaultCudaVectorThresholdDimension,
            cudaAvailable: false);

        Assert.Equal(GmresVectorExecutionMode.Cpu, mode);
    }

    [Fact]
    public void Factory_PropagatesCudaAccelerationFlagToGmresSolver()
    {
        var solver = Assert.IsType<GmresSolver>(NumericSolverFactory.Create(
            NumericSolver.GMRES,
            useCudaAcceleration: true));

        Assert.True(solver.UsesCudaAcceleration);
    }

    [Fact]
    public void SolveLinearSystem_FallsBackToCpuWhenCudaIsDisabledAtRuntime()
    {
        const int n = GmresSolver.DefaultCudaVectorThresholdDimension;
        var matrix = SparseMatrix.Create(n, n, 0.0);
        for (int i = 0; i < n; i++)
            matrix[i, i] = 2.0;

        var rhs = DenseVector.Create(n, _ => 4.0);
        bool? previousAvailabilityOverride = GmresCudaContext.AvailabilityOverride;

        try
        {
            GmresCudaContext.AvailabilityOverride = false;

            var solver = new GmresSolver(useCudaAcceleration: true);
            var solution = solver.SolveLinearSystem(matrix, rhs);

            Assert.Equal(GmresVectorExecutionMode.Cpu, solver.LastVectorExecutionMode);
            Assert.Equal(2.0, solution[0], 6);
            Assert.Equal(2.0, solution[n / 2], 6);
            Assert.Equal(2.0, solution[n - 1], 6);
        }
        finally
        {
            GmresCudaContext.AvailabilityOverride = previousAvailabilityOverride;
        }
    }
}
