using System.Linq;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers.FiniteElementSolver;
using Xunit;

namespace Utility.Tests;

public class FemMeshQualityTests
{
    [Fact]
    public void CircularMesh_HighResolution_DoesNotCreateDegenerateElements()
    {
        var mesh = MeshFactory.CreateCircularFEMMesh(
            layers: 4,
            boundaryFEMVertexCount: 50,
            electrodeCount: 16,
            inhomogeneityValue: 1.0,
            nodesPerElectrode: 1,
            electrodeLengthHint: 0.3);

        var elements = mesh.GetElements().Cast<FEMElement>().ToList();

        Assert.NotEmpty(elements);
        Assert.All(elements, element =>
        {
            Assert.True(element.Area > 1e-10, "Element area fell below stability threshold.");

            var vertices = element.Vertices;
            Assert.True(DistanceSquared(vertices[0], vertices[1]) > 1e-12, "Edge length too small (v0-v1).");
            Assert.True(DistanceSquared(vertices[1], vertices[2]) > 1e-12, "Edge length too small (v1-v2).");
            Assert.True(DistanceSquared(vertices[2], vertices[0]) > 1e-12, "Edge length too small (v2-v0).");
        });
    }

    [Fact]
    public void CircularMesh_HighResolution_ForwardSolveRemainsFinite()
    {
        var mesh = MeshFactory.CreateCircularFEMMesh(
            layers: 4,
            boundaryFEMVertexCount: 50,
            electrodeCount: 16,
            inhomogeneityValue: 1.0,
            nodesPerElectrode: 1,
            electrodeLengthHint: 0.3);

        var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
        var boundaryCondition = new FEMBoundaryCondition(electrodes);
        var solver = new FiniteElementSolver(mesh, new SVDSolver());

        var potentials = solver.SolveForward(mesh, boundaryCondition);

        Assert.All(potentials.Potentials.Values, value => Assert.True(double.IsFinite(value), "Non-finite potential detected."));
    }

    private static double DistanceSquared(FEMVertex a, FEMVertex b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
