namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericSolver
    {
        LUDecomposition = 1,
        SVD = 2,
        tSVD = 3,
        GMRES = 4
    };

    public interface ILinearSolver
    {
        double[] SolveLinearSystem(double[,] A, double[] b);
    }

    public sealed class LuDecompositionSolver : ILinearSolver
    {
        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            // TODO: use MathNet.Numerics or your own LU
            return b;
        }
    }

    public sealed class SVDSolver : ILinearSolver 
    {
        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            // TODO: use MathNet.Numerics or your own LU
            return b;
        }
    }

    public sealed class tSVDSolver : ILinearSolver
    {
        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            // TODO: use MathNet.Numerics or your own LU
            return b;
        }
    }

    public sealed class GmresSolver : ILinearSolver 
    {
        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            // TODO: use MathNet.Numerics or your own LU
            return b;
        }
    }

    public static class LinearSolverFactory
    {
        public static ILinearSolver Create(NumericSolver ns) => ns switch
        {
            NumericSolver.LUDecomposition => new LuDecompositionSolver(),
            NumericSolver.SVD => new SVDSolver(),
            NumericSolver.tSVD => new tSVDSolver(),
            NumericSolver.GMRES => new GmresSolver(),
            _ => throw new NotSupportedException()
        };
    }
}
