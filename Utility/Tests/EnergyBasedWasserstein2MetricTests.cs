using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Reconstruction.ErrorMetrics;
using Xunit;

namespace Utility.Tests
{
    public sealed class EnergyBasedWasserstein2MetricTests
    {
        [Fact]
        public void Evaluate_ReturnsZero_ForIdenticalHistograms()
        {
            var mesh = TinyFEM();
            var metric = new EnergyBasedWasserstein2Metric();

            double[] histogram = { 0.25, 0.25, 0.25, 0.25 };

            double cost = metric.Evaluate(mesh, histogram, histogram);
            Assert.Equal(0.0, cost, 10);

            var adjoint = metric.EvaluateAdjointSource(mesh, histogram, histogram);
            Assert.Equal(histogram.Length, adjoint.Length);
            Assert.True(adjoint.All(v => Math.Abs(v) < 1e-8));
        }

        [Fact]
        public void Evaluate_ComputesPositiveCost_ForShiftedMass()
        {
            var mesh = TinyFEM();
            var metric = new EnergyBasedWasserstein2Metric();

            double[] measured = { 0.7, 0.3, 0.0, 0.0 };
            double[] simulated = { 0.0, 1.0, 0.0, 0.0 };

            double cost = metric.Evaluate(mesh, measured, simulated);
            Assert.True(cost > 0.0);

            var adjoint = metric.EvaluateAdjointSource(mesh, measured, simulated);
            Assert.Equal(measured.Length, adjoint.Length);
            Assert.True(Math.Abs(adjoint.Sum()) < 1e-10);
        }

        private static FEMMesh TinyFEM()
        {
            var v1 = new FEMVertex { GlobalId = 0, X = 0.0, Y = 0.0, IsElectrode = true };
            var v2 = new FEMVertex { GlobalId = 1, X = 1.0, Y = 0.0, IsElectrode = true };
            var v3 = new FEMVertex { GlobalId = 2, X = 1.0, Y = 1.0, IsElectrode = true };
            var v4 = new FEMVertex { GlobalId = 3, X = 0.0, Y = 1.0, IsElectrode = true };

            var elements = new List<FEMElement>
            {
                new FEMElement(0, v1, v2, v3),
                new FEMElement(1, v1, v3, v4)
            };

            var electrodes = new List<FEMElectrode>();
            var nodes = new[] { v1, v2, v3, v4 };
            for (int i = 0; i < nodes.Length; i++)
            {
                var electrode = new FEMElectrode(i, nodes[i].GlobalId, 0.0, 0.1, 0.0, isMeasuring: true);
                electrode.FEMVertexIds.Add(nodes[i].GlobalId);
                electrode.Length = 1.0;
                electrodes.Add(electrode);
            }

            return new FEMMesh(nodes, elements, electrodes);
        }
    }
}
