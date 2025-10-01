using System;
using Utility.Classes.ReconstructionParameters;
using Xunit;

namespace Utility.Tests
{
    public class Wasserstein2Tests
    {
        [Fact]
        public void T0_UniformMassesYieldZero()
        {
            // When both distributions are uniform the OT problem becomes
            // degenerate.  The helper should simply return zero cost and
            // gradient instead of throwing.
            double[] m = { 5.0, 5.0, 5.0 };
            double[] d = { 1.0, 1.0, 1.0 };
            var coords = new (double, double)[] { (0, 0), (1, 0), (2, 0) };
            var res = Wasserstein2ErrorMetric.w2_misfit_and_grad(m, d, coords, coords);
            Assert.Equal(0.0, res.Cost, 12);
            Assert.All(res.Grad, g => Assert.Equal(0.0, g, 9));
        }

        [Fact]
        public void T1_ExactMatch()
        {
            double[] m = { 1.0, 2.0, 3.0 };
            double[] d = { 1.0, 2.0, 3.0 };
            var coords = new (double, double)[] { (0,0), (1,0), (2,0) };
            var res = Wasserstein2ErrorMetric.w2_misfit_and_grad(m, d, coords, coords);
            Assert.Equal(0.0, res.Cost, 12);
            foreach (var g in res.Grad)
                Assert.Equal(0.0, g, 9);
        }

        [Fact]
        public void T2_Translation()
        {
            double[] m = { 1.0, 1.0 };
            double[] d = { 1.0, 1.0 };
            var x = new (double, double)[] { (0,0), (1,0) };
            var y = new (double, double)[] { (1,0), (2,0) }; // shift by +1
            var res = Wasserstein2ErrorMetric.w2_misfit_and_grad(m, d, x, y);
            Assert.Equal(0.5, res.Cost, 6);
            Assert.True(res.Grad[0] < res.Grad[1]);
        }

        [Fact]
        public void T3_ScalingInvariant()
        {
            double[] m1 = { 2.0, 1.0 };
            double[] m2 = { 20.0, 10.0 }; // scaled
            double[] d = { 0.0, 3.0 };
            var coords = new (double, double)[] { (0,0), (1,0) };
            var r1 = Wasserstein2ErrorMetric.w2_misfit_and_grad(m1, d, coords, coords);
            var r2 = Wasserstein2ErrorMetric.w2_misfit_and_grad(m2, d, coords, coords);
            Assert.Equal(r1.Cost, r2.Cost, 9);
            Assert.Equal(r1.Grad[0], r2.Grad[0], 6);
            Assert.Equal(r1.Grad[1], r2.Grad[1], 6);
        }

        [Fact]
        public void T4_PrimalDualConsistency()
        {
            double[] m = { 1.0, 1.0 };
            double[] d = { 1.0, 1.0 };
            var x = new (double, double)[] { (0,0), (1,0) };
            var y = new (double, double)[] { (0,0), (1,0) };
            var res = Wasserstein2ErrorMetric.w2_misfit_and_grad(m, d, x, y);
            for (int i = 0; i < x.Length; i++)
                for (int j = 0; j < y.Length; j++)
                {
                    double cij = Math.Pow(x[i].Item1 - y[j].Item1, 2) + Math.Pow(x[i].Item2 - y[j].Item2, 2);
                    Assert.True(res.Phi[i] + res.Psi[j] <= cij + 1e-8);
                    if (res.Plan[i,j] > 1e-8)
                        Assert.True(Math.Abs(res.Phi[i] + res.Psi[j] - cij) <= 1e-6);
                }
        }

        [Fact]
        public void T5_FiniteDifferenceGradient()
        {
            double[] m = { 1.0, 2.0 };
            double[] d = { 0.0, 3.0 };
            var coords = new (double, double)[] { (0,0), (1,0) };
            var res = Wasserstein2ErrorMetric.w2_misfit_and_grad(m, d, coords, coords);
            double eps = 1e-6;
            double[] mPert = (double[])m.Clone();
            mPert[0] += eps;
            var resPert = Wasserstein2ErrorMetric.w2_misfit_and_grad(mPert, d, coords, coords);
            double fd = (resPert.Cost - res.Cost) / eps;
            Assert.Equal(fd, res.Grad[0], 4);
        }
    }
}
