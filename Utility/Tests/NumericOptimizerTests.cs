using Utility.Classes.Factories;
using Utility.Classes.ReconstructionParameters;
using Xunit;

namespace Utility.Tests
{
    public class NumericOptimizerTests
    {
        [Fact]
        public void GradientBased_Clamps_And_Steps()
        {
            var opt = new GradientBasedOptimizer(); // min=1e-6, max=10.0  
            var σk = TestData.Sigma((0, 9.9), (1, 1.0), (2, 1e-7));
            var g = TestData.Grad((0, -10.0), (1, 2.0), (2, 10.0)); // negative grad increases value

            var σnext = opt.OptimizationStep(σk, g, stepSize: 0.2);
            // id0: 9.9 - 0.2*(-10) = 11.9 → clamp to 10.0
            // id1: 1.0 - 0.2*( 2)  = 0.6
            // id2: 1e-7 - 0.2*(10) = -2.0 → clamp to 1e-6
            Assert.Equal(10.0, σnext.GetConductivity(0), 12);
            Assert.Equal(0.6, σnext.GetConductivity(1), 12);
            Assert.Equal(1e-6, σnext.GetConductivity(2), 12);
        }

        [Fact]
        public void PolyakHeavyBall_Uses_Momentum_From_Second_Call()
        {
            var opt = new PolyakHeavyBallOptimizer(beta: 0.5);
            var σ0 = TestData.Sigma((0, 1.0));
            var g = TestData.Grad((0, 1.0));

            var σ1 = opt.OptimizationStep(σ0, g, 0.1); // v0=0 → v1 = -0.1, σ1=0.9
            Assert.Equal(0.9, σ1.GetConductivity(0), 12);

            var σ2 = opt.OptimizationStep(σ1, g, 0.1); // v2 = 0.5*(-0.1) - 0.1*1 = -0.15, σ2=0.75
            Assert.Equal(0.75, σ2.GetConductivity(0), 12);
        }

        [Fact]
        public void Adam_Moves_Against_Gradient()
        {
            var opt = new AdamGradientOptimizer(); 
            var σ = TestData.Sigma((0, 1.0));
            var g = TestData.Grad((0, 1.0));

            var σ1 = opt.OptimizationStep(σ, g, 0.1);
            Assert.True(σ1.GetConductivity(0) < 1.0);
        }

        [Fact]
        public void Nesterov_Uses_PrevSigma()
        {
            var opt = new NesterovAcceleratedGradientOptimizer(gamma: 0.9);
            var σ0 = TestData.Sigma((0, 1.0));
            var g = TestData.Grad((0, 1.0));

            var σ1 = opt.OptimizationStep(σ0, g, 0.1);
            var σ2 = opt.OptimizationStep(σ1, g, 0.1);
            Assert.True(σ2.GetConductivity(0) < σ1.GetConductivity(0));
        }

        [Fact]
        public void SimulatedAnnealing_Is_Idempotent_When_Step_Is_Zero()
        {
            var opt = new SimulatedAnnealingOptimizer();
            var σ0 = TestData.Sigma((0, 1.0), (1, 2.0));
            var g = TestData.Grad((0, 0.5), (1, -0.5));

            var σ1 = opt.OptimizationStep(σ0, g, 0.0);
            Assert.Equal(1.0, σ1.GetConductivity(0), 12);
            Assert.Equal(2.0, σ1.GetConductivity(1), 12);
        }
    }
}
