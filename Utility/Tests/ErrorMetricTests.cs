using System.Linq;
using Utility.Classes.Factories;
using Utility.Classes.Reconstruction.ErrorMetrics;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Xunit;

namespace Utility.Tests
{
    public class ErrorMetricTests
    {
        [Fact]
        public void L2_Evaluate_Works_And_Ignores_NaNs()
        {
            var mesh = new FakeMesh(nElectrodes: 4);
            var l2 = new L2ErrorMetric();

            double[] measured = { 1.0, 2.0, double.NaN, 4.0 };
            double[] simulated = { 1.5, 1.0, 3.0, double.NaN };

            // residuals used: (1.5-1.0)=0.5 → 0.25; (1.0-2.0)=-1 → 1; NaN ignored; NaN ignored
            // sum=1.25; J=0.5*1.25=0.625
            var j = l2.Evaluate(mesh, measured, simulated);
            Assert.Equal(0.625, j, 6);

            var adj = l2.EvaluateAdjointSource(mesh, measured, simulated);
            // adjoint is measured - simulated, where both valid; else 0
            Assert.Equal(new[] { -0.5, 1.0, 0.0, 0.0 }, adj, new DoubleArrayComparer(1e-12));
        }

        // W2 is an integration test because it needs LBMGrid + OR-Tools.
        [Fact]
        public void Wasserstein2_LBM_Includes_Excitation_Electrodes()
        {
            // Create a small grid with two electrodes.  One electrode is
            // flagged purely as an excitation (IsMeasuring=false) but still
            // carries a measured potential.  The W₂ metric should include this
            // electrode in the transport problem.
            var grid = new LBMGrid(5, 5);
            grid.PlaceEquidistantElectrodes(2);

            var els = grid.GetElectrodes().Cast<LBMElectrode>().ToList();
            els[0].IsExcitation = true;
            els[0].IsMeasuring = false; // active electrode with measurement
            els[1].IsMeasuring = true;
            grid.SetElectrodes(els);

            // Distributions differing on the excitation electrode
            double[] meas = { 1.0, 0.0 };
            double[] sim = { 0.0, 1.0 };

            IErrorMetric w2 = new Wasserstein2ErrorMetric();
            double val = w2.Evaluate(grid, meas, sim);
            Assert.True(val > 0.0);

            var grad = w2.EvaluateAdjointSource(grid, meas, sim);
            Assert.Equal(2, grad.Length);
            Assert.NotEqual(0.0, grad[0], 12); // excitation electrode included
        }

        [Fact]
        public void Wasserstein2_FEM_Evaluate_Produces_Finite_Value()
        {
            var mesh = TinyFEM();

            // Simple distributions over 4 electrodes
            double[] meas = { 0.0, 1.0, 0.0, 0.0 };
            double[] sim = { 1.0, 0.0, 0.0, 0.0 };

            IErrorMetric w2 = new Wasserstein2ErrorMetric();
            double val = w2.Evaluate(mesh, meas, sim);
            Assert.True(val >= 0.0);

            var phi = w2.EvaluateAdjointSource(mesh, meas, sim);
            Assert.Equal(meas.Length, phi.Length);
        }

        [Fact]
        public void ErrorMetricFactory_Returns_ConductivityAwareW2Metric()
        {
            var metric = ErrorMetricFactory.Create(ErrorMetric.ConductivityAwareW2);

            Assert.IsType<ConductivityAwareW2Metric>(metric);
        }

        [Fact]
        public void ErrorMetricFactory_Returns_EnergyBasedWasserstein2Metric()
        {
            var metric = ErrorMetricFactory.Create(ErrorMetric.EnergyBasedWasserstein2);

            Assert.IsType<EnergyBasedWasserstein2Metric>(metric);
        }

        private static FEMMesh TinyFEM()
        {
            var v1 = new FEMVertex { GlobalId = 0, X = 0, Y = 0, IsElectrode = true };
            var v2 = new FEMVertex { GlobalId = 1, X = 1, Y = 0, IsElectrode = true };
            var v3 = new FEMVertex { GlobalId = 2, X = 1, Y = 1, IsElectrode = true };
            var v4 = new FEMVertex { GlobalId = 3, X = 0, Y = 1, IsElectrode = true };

            var elems = new List<FEMElement>
            {
                new FEMElement(0, v1, v2, v3),
                new FEMElement(1, v1, v3, v4)
            };

            var electrodes = new List<FEMElectrode>
            {
                new FEMElectrode(0, 0, 0.0, 0.0, 0.0, isMeasuring: true),
                new FEMElectrode(1, 1, 0.0, 0.0, 0.0, isMeasuring: true),
                new FEMElectrode(2, 2, 0.0, 0.0, 0.0, isMeasuring: true),
                new FEMElectrode(3, 3, 0.0, 0.0, 0.0, isMeasuring: true)
            };

            return new FEMMesh(new[] { v1, v2, v3, v4 }, elems, electrodes);
        }

        private sealed class DoubleArrayComparer : IEqualityComparer<double[]>
        {
            private readonly double _tol;
            public DoubleArrayComparer(double tol) => _tol = tol;
            public bool Equals(double[]? x, double[]? y)
            {
                if (x == null || y == null || x.Length != y.Length) return false;
                for (int i = 0; i < x.Length; i++)
                    if (Math.Abs(x[i] - y[i]) > _tol) return false;
                return true;
            }
            public int GetHashCode(double[] obj) => obj.Length;
        }
    }
}
