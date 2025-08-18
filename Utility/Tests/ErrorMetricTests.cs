using Utility.Classes.ReconstructionParameters;
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

        // Optional: W2 is an integration test because it needs LBMMesh + OR-Tools.
        // You can enable this on CI runners that have the dependency available.
        [Fact(Skip = "Enable when OR-Tools + LBMMesh are wired; this is an integration test.")]
        public void Wasserstein2_Evaluate_Produces_Finite_Value()
        {
            // Arrange a tiny LBMMesh with 4 electrodes and deterministic coordinates,
            /// TODO: validate W2
        }

        private sealed class DoubleArrayComparer : IEqualityComparer<double[]>
        {
            private readonly double _tol;
            public DoubleArrayComparer(double tol) => _tol = tol;
            public bool Equals(double[] x, double[] y)
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
