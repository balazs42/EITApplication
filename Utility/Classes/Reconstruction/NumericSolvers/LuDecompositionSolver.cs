using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    /// <summary>
    /// Solves Ax = b using LU decomposition. Best for square, well-conditioned, non-singular matrices.
    /// This is a fast and direct solver.
    /// </summary>
    public sealed class LuDecompositionSolver : INumericSolver
    {
        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            if (A.Cast<double>().Any(d => double.IsNaN(d) || double.IsInfinity(d)) ||
                                     b.Any(d => double.IsNaN(d) || double.IsInfinity(d)))
                throw new InvalidOperationException("System contains invalid entries.");

            // Convert native C# arrays to MathNet types
            Matrix<double> matrixA = DenseMatrix.OfArray(A);
            Vector<double> vectorB = DenseVector.OfArray(b);

            if (matrixA.RowCount != matrixA.ColumnCount)
                throw new ArgumentException("LU decomposition requires a square matrix.");

            if (Math.Abs(matrixA.Determinant()) < 1e-12)
                throw new InvalidOperationException("Matrix is singular or nearly so.");

            // Perform LU decomposition and solve
            var lu = matrixA.LU();
            Vector<double> resultX = lu.Solve(vectorB);

            // Convert result back to a native double array
            return resultX.ToArray();
        }
    }
}
