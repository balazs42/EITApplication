using Utility.Classes.Discretizer;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes.Reconstruction.VirtualElectrodes.Estimators;
using Xunit;

namespace Utility.Tests;

public class VirtualElectrodeEstimatorTests
{
    private sealed class TestElectrode : Electrode
    {
        public TestElectrode(int id, bool isVirtual = false)
        {
            Id = id;
            IsVirtual = isVirtual;
            IsMeasuring = true;
        }
    }

    [Fact]
    public void GeometricEstimator_InterpolatesMidpoints()
    {
        var electrodes = new List<Electrode>
        {
            new TestElectrode(0),
            new TestElectrode(1, isVirtual: true),
            new TestElectrode(2),
            new TestElectrode(3, isVirtual: true),
            new TestElectrode(4)
        };

        var angles = new Dictionary<int, double>
        {
            [0] = 0.0,
            [1] = Math.PI / 4.0,
            [2] = Math.PI / 2.0,
            [3] = 3.0 * Math.PI / 4.0,
            [4] = Math.PI
        };

        var estimator = new GeometricVirtualElectrodeEstimator();
        var settings = new VirtualElectrodeSettings
        {
            UseVirtualElectrodes = true,
            Method = VirtualElectrodeMethod.GeometricInterpolation
        };
        double[] measured = { 10.0, 20.0, 30.0 };

        var completed = estimator.CompleteElectrodePotentials(
            electrodes,
            measured,
            settings,
            new ForwardModelContext
            {
                ElectrodeAngles = angles,
                RealElectrodeCount = 3
            });

        Assert.Equal(5, completed.Length);
        Assert.Equal(10.0, completed[0], 6);
        Assert.Equal(20.0, completed[2], 6);
        Assert.Equal(30.0, completed[4], 6);
        Assert.Equal((10.0 + 20.0) / 2.0, completed[1], 6);
        Assert.Equal((20.0 + 30.0) / 2.0, completed[3], 6);
    }

    [Fact]
    public void LinearCombinationEstimator_UsesConfiguredAlpha()
    {
        var electrodes = new List<Electrode>
        {
            new TestElectrode(0),
            new TestElectrode(1, isVirtual: true),
            new TestElectrode(2)
        };

        var angles = new Dictionary<int, double>
        {
            [0] = 0.0,
            [1] = Math.PI / 4.0,
            [2] = Math.PI / 2.0
        };

        var estimator = new LinearCombinationVirtualElectrodeEstimator();
        var settings = new VirtualElectrodeSettings
        {
            UseVirtualElectrodes = true,
            Method = VirtualElectrodeMethod.LinearCombination,
            LinearCombinationAlpha = 0.75
        };

        double[] measured = { 4.0, 10.0 };

        var completed = estimator.CompleteElectrodePotentials(
            electrodes,
            measured,
            settings,
            new ForwardModelContext
            {
                ElectrodeAngles = angles,
                RealElectrodeCount = 2
            });

        double expectedVirtual = (1.0 - 0.75) * 4.0 + 0.75 * 10.0;
        Assert.Equal(3, completed.Length);
        Assert.Equal(expectedVirtual, completed[1], 6);
    }

    [Fact]
    public void HarrachEstimator_UsesJacobianToPredictVirtualChannels()
    {
        var electrodes = new List<Electrode>
        {
            new TestElectrode(0),
            new TestElectrode(1),
            new TestElectrode(2, isVirtual: true)
        };

        var estimator = new HarrachVirtualElectrodeEstimator();
        var settings = new VirtualElectrodeSettings
        {
            UseVirtualElectrodes = true,
            Method = VirtualElectrodeMethod.HarrachSensitivityInterpolation,
            HarrachLambda = 1e-3
        };

        double[] measured = { 10.0, 20.0 };
        var jacobian = new double[,]
        {
            { 1.0 },
            { 2.0 },
            { 3.0 }
        };

        var completed = estimator.CompleteElectrodePotentials(
            electrodes,
            measured,
            settings,
            new ForwardModelContext
            {
                RealElectrodeCount = 2,
                Jacobian = jacobian
            });

        Assert.Equal(3, completed.Length);
        Assert.Equal(10.0, completed[0], 6);
        Assert.Equal(20.0, completed[1], 6);
        double expectedVirtual = 3.0 * (50.0 / 5.001);
        Assert.Equal(expectedVirtual, completed[2], 3);
    }

    [Fact]
    public void NdMapEstimator_ReconstructsSpectralSamples()
    {
        var electrodes = new List<Electrode>
        {
            new TestElectrode(0),
            new TestElectrode(1),
            new TestElectrode(2),
            new TestElectrode(3),
            new TestElectrode(4, isVirtual: true)
        };

        var angles = new Dictionary<int, double>
        {
            [0] = 0.0,
            [1] = Math.PI / 2.0,
            [2] = Math.PI,
            [3] = 3.0 * Math.PI / 2.0,
            [4] = Math.PI / 4.0
        };

        var estimator = new NdMapVirtualElectrodeEstimator();
        var settings = new VirtualElectrodeSettings
        {
            UseVirtualElectrodes = true,
            Method = VirtualElectrodeMethod.NdMapSpectralInterpolation,
            NdMaxMode = 3
        };

        double[] measured =
        {
            Math.Sin(angles[0]),
            Math.Sin(angles[1]),
            Math.Sin(angles[2]),
            Math.Sin(angles[3])
        };

        var completed = estimator.CompleteElectrodePotentials(
            electrodes,
            measured,
            settings,
            new ForwardModelContext
            {
                ElectrodeAngles = angles,
                RealElectrodeCount = 4
            });

        Assert.Equal(5, completed.Length);
        double expectedVirtual = Math.Sin(angles[4]);
        Assert.InRange(completed[4], expectedVirtual - 1e-5, expectedVirtual + 1e-5);
    }
}
