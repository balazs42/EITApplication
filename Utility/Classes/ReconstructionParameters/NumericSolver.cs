namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericSolver
    {
        LU = 1,
        SVD = 2,
        tSVD = 3,
        GMRES = 4,
        ConjugateGradient = 5
    };

    public interface INumericSolver
    {
        MathNet.Numerics.LinearAlgebra.Vector<double> SolveLinearSystem(
            MathNet.Numerics.LinearAlgebra.Matrix<double> A,
            MathNet.Numerics.LinearAlgebra.Vector<double> b);
    }
}