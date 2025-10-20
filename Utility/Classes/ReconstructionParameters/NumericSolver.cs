namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericSolver
    {
        LU = 1,
        SVD = 2,
        tSVD = 3,
        GMRES = 4
    };

    public interface INumericSolver
    {
        double[] SolveLinearSystem(double[,] A, double[] b);
    }
}