using Utility.Classes.Factories;
using Utility.Classes.ReconstructionParameters;
using Xunit;

namespace Utility.Tests
{
    public class NumericSolverTests
    {
        [Fact]
        public void LU_Solves_2x2()
        {
            INumericSolver s = NumericSolverFactory.Create(NumericSolver.LU);
            double[,] A = { { 3, 2 }, { 1, 2 } };
            double[] b = { 5, 5 };

            var x = s.SolveLinearSystem(A, b);
            // Solve: 3x+2y=5; x+2y=5 → subtract: 2x=0 → x=0 → y=2.5
            Assert.Equal(0.0, x[0], 12);
            Assert.Equal(2.5, x[1], 12);
        }

        [Fact]
        public void SVD_Solves_Overdetermined()
        {
            INumericSolver s = NumericSolverFactory.Create(NumericSolver.SVD);
            double[,] A = { { 1, 0 }, { 0, 1 }, { 1, 1 } };
            double[] b = { 1, 2, 3 };

            var x = s.SolveLinearSystem(A, b); // least-squares min-norm
            Assert.Equal(1.0, x[0], 6);
            Assert.Equal(2.0, x[1], 6);
        }

        [Fact]
        public void tSVD_Filters_Small_Singulars()
        {
            INumericSolver s = NumericSolverFactory.Create(NumericSolver.tSVD);
            double[,] A = { { 1, 0 }, { 0, 1e-12 } };
            double[] b = { 1, 1 };

            var x = s.SolveLinearSystem(A, b);
            // second singular truncated → x2 ≈ 0
            Assert.Equal(1.0, x[0], 6);
            Assert.True(Math.Abs(x[1]) < 1e-6);
        }

        [Fact]
        public void GMRES_Solves_Diagonal()
        {
            INumericSolver s = NumericSolverFactory.Create(NumericSolver.GMRES);
            double[,] A = { { 4, 0 }, { 0, 2 } };
            double[] b = { 8, 6 };

            var x = s.SolveLinearSystem(A, b);
            Assert.Equal(2.0, x[0], 6);
            Assert.Equal(3.0, x[1], 6);
        }
    }
}
