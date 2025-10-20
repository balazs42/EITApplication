using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    /// <summary>
    /// Solves Ax = b using Singular Value Decomposition (SVD).
    /// This is a very robust solver that can handle non-square or ill-conditioned matrices
    /// by finding the minimum-norm, least-squares solution.
    /// </summary>
    public sealed class SVDSolver : INumericSolver
    {
        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            if (A.Cast<double>().Any(d => double.IsNaN(d) || double.IsInfinity(d)) ||
                b.Any(d => double.IsNaN(d) || double.IsInfinity(d)))
                throw new InvalidOperationException("SVD solver received non-finite entries. This typically indicates a degenerate FEM element (zero area) in the mesh assembly.");

            Matrix<double> matrixA = DenseMatrix.OfArray(A);
            Vector<double> vectorB = DenseVector.OfArray(b);

            // Perform SVD and solve
            var svd = matrixA.Svd();
            Vector<double> resultX = svd.Solve(vectorB);

            return resultX.ToArray();
        }
    }
}
