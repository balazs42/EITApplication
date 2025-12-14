using Utility.Classes.Reconstruction.Regulizers;
using Xunit;

namespace Utility.Tests
{
    public class RegularizerTests
    {
        [Fact]
        public void NoRegularizer_Is_Zero()
        {
            var reg = new NoRegularizer();
            var mesh = new FakeMesh(3);
            var σ = TestData.Sigma((0, 1.0), (1, 2.0), (2, 3.0));

            Assert.Equal(0.0, reg.EvaluateTerm(mesh, σ), 12);
            var g = reg.EvaluateGradient(mesh, σ);
            Assert.Equal(0.0, g.GetConductivity(0), 12);
            Assert.Equal(0.0, g.GetConductivity(1), 12);
            Assert.Equal(0.0, g.GetConductivity(2), 12);
        }

        [Fact]
        public void ZeroOrderTikhonov_Value_And_Gradient()
        {
            var σprior = TestData.Sigma((0, 1.0), (1, 1.0), (2, 1.0));
            var reg = new ZeroOrderTikhonov(σprior, lambda: 2e-1); // λ=0.2 
            var mesh = new FakeMesh(3);
            var σ = TestData.Sigma((0, 2.0), (1, 1.0), (2, -1.0));

            // residuals: (1,0,-2) → sumsq=1+0+4=5 → J=0.5*λ*5 = 0.5*0.2*5 = 0.5
            var val = reg.EvaluateTerm(mesh, σ);
            Assert.Equal(0.5, val, 12);

            // grad = λ*(σ-σprior) = 0.2*(1,0,-2) = (0.2,0,-0.4)
            var g = reg.EvaluateGradient(mesh, σ);
            Assert.Equal(0.2, g.GetConductivity(0), 12);
            Assert.Equal(0.0, g.GetConductivity(1), 12);
            Assert.Equal(-0.4, g.GetConductivity(2), 12);
        }

        // FirstOrder/Laplace/TV require FEM/LBM operator helpers TODO: implement
    }
}
