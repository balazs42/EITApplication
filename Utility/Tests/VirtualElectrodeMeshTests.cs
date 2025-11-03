using System.Linq;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Factories;
using Utility.Classes.VirtualElectrodes;
using Xunit;

namespace Utility.Tests;

public class VirtualElectrodeMeshTests
{
    [Fact]
    public void FemMesh_ApplyVirtualElectrodes_AddsAndRemovesVirtualContacts()
    {
        var mesh = MeshFactory.CreateCircularFEMMesh(
            layers: 1,
            boundaryFEMVertexCount: 24,
            electrodeCount: 4,
            inhomogeneityValue: 1.0,
            nodesPerElectrode: 1,
            electrodeLengthHint: 0.3);

        var settings = new VirtualElectrodeSettings
        {
            UseVirtualElectrodes = true,
            VirtualElectrodesPerGap = 1
        };

        mesh.ApplyVirtualElectrodes(settings, defaultZContact: 0.1);

        var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
        Assert.Equal(8, electrodes.Count);
        Assert.Equal(4, electrodes.Count(e => e.IsVirtual));
        Assert.All(electrodes.Where(e => e.IsVirtual), e =>
        {
            Assert.True(e.PointElectrode);
            Assert.True(e.FEMVertexIds.Count == 1);
        });

        settings.UseVirtualElectrodes = false;
        mesh.ApplyVirtualElectrodes(settings, defaultZContact: 0.1);

        electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
        Assert.Equal(4, electrodes.Count);
        Assert.DoesNotContain(electrodes, e => e.IsVirtual);
    }

    [Fact]
    public void LbmGrid_ApplyVirtualElectrodes_TogglesVirtualCells()
    {
        var grid = MeshFactory.CreateRectangularLBMGrid(nx: 21, ny: 21, electrodeCount: 4);
        var settings = new VirtualElectrodeSettings
        {
            UseVirtualElectrodes = true,
            VirtualElectrodesPerGap = 1
        };

        grid.ApplyVirtualElectrodes(settings);

        var electrodes = grid.GetElectrodes().Cast<LBMElectrode>().ToList();
        Assert.Equal(8, electrodes.Count);
        Assert.Equal(4, electrodes.Count(e => e.IsVirtual));
        Assert.All(electrodes.Where(e => e.IsVirtual), e => Assert.True(e.IsMeasuring));

        settings.UseVirtualElectrodes = false;
        grid.ApplyVirtualElectrodes(settings);

        electrodes = grid.GetElectrodes().Cast<LBMElectrode>().ToList();
        Assert.Equal(4, electrodes.Count);
        Assert.DoesNotContain(electrodes, e => e.IsVirtual);
    }
}
