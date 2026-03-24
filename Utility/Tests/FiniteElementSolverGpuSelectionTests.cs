using System.Linq;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction.NumericSolvers;
using Utility.Classes.Solvers.FiniteElementSolver;
using Xunit;

namespace Utility.Tests;

public class FiniteElementSolverGpuSelectionTests
{
    [Fact]
    public void ShouldUseCudaForReconstruction_ReturnsFalseForSmallMeshes()
    {
        var mesh = MeshFactory.CreateCircularFEMMesh(
            layers: 2,
            boundaryFEMVertexCount: 24,
            electrodeCount: 16,
            inhomogeneityValue: 1.0,
            nodesPerElectrode: 1,
            electrodeLengthHint: 0.3);

        Assert.False(FiniteElementGpuExecutionPolicy.ShouldUseCudaForReconstruction(mesh));
    }

    [Fact]
    public void ShouldUseCudaForReconstruction_ReturnsTrueForLargeMeshes()
    {
        var mesh = MeshFactory.CreateCircularFEMMesh(
            layers: 10,
            boundaryFEMVertexCount: 220,
            electrodeCount: 16,
            inhomogeneityValue: 1.0,
            nodesPerElectrode: 1,
            electrodeLengthHint: 0.3);

        Assert.True(FiniteElementGpuExecutionPolicy.ShouldUseCudaForReconstruction(mesh));
    }

    [Fact]
    public void SelectStiffnessAssemblyMode_UsesCudaForLargeMeshesWhenAvailable()
    {
        var mode = FiniteElementSolver.SelectStiffnessAssemblyMode(
            useOmpParallelization: true,
            useCudaAcceleration: true,
            elementCount: FiniteElementSolver.DefaultCudaAssemblyThresholdElements,
            cudaAvailable: true);

        Assert.Equal(FiniteElementAssemblyMode.Cuda, mode);
    }

    [Fact]
    public void SelectStiffnessAssemblyMode_FallsBackToParallelCpuWhenCudaIsUnavailable()
    {
        var mode = FiniteElementSolver.SelectStiffnessAssemblyMode(
            useOmpParallelization: true,
            useCudaAcceleration: true,
            elementCount: FiniteElementSolver.DefaultCudaAssemblyThresholdElements,
            cudaAvailable: false);

        Assert.Equal(FiniteElementAssemblyMode.ParallelCpu, mode);
    }

    [Fact]
    public void ForwardSolve_FallsBackToCpuWhenCudaIsDisabledAtRuntime()
    {
        var mesh = MeshFactory.CreateCircularFEMMesh(
            layers: 10,
            boundaryFEMVertexCount: 220,
            electrodeCount: 16,
            inhomogeneityValue: 1.0,
            nodesPerElectrode: 1,
            electrodeLengthHint: 0.3);

        var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
        electrodes[0].IsExcitation = true;
        electrodes[0].IsMeasuring = false;
        electrodes[0].Current = 1.0;
        electrodes[1].IsGround = true;
        electrodes[1].IsMeasuring = false;
        electrodes[1].Current = -1.0;

        bool? previousAvailabilityOverride = FiniteElementCudaContext.AvailabilityOverride;

        try
        {
            FiniteElementCudaContext.AvailabilityOverride = false;

            var solver = new FiniteElementSolver(
                mesh,
                new GmresSolver(),
                useOmpParallelization: true,
                useCudaAcceleration: true);

            _ = solver.SolveForward(mesh, new FEMBoundaryCondition(electrodes));

            Assert.Equal(FiniteElementAssemblyMode.ParallelCpu, solver.LastStiffnessAssemblyMode);
            Assert.All(mesh.GetPotentialDistribution().Potentials.Values, value => Assert.True(double.IsFinite(value)));
        }
        finally
        {
            FiniteElementCudaContext.AvailabilityOverride = previousAvailabilityOverride;
        }
    }
}
