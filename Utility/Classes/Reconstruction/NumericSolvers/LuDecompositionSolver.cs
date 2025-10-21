using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    /// <summary>
    /// Solves Ax = b using LU decomposition. Best for square, well-conditioned, non-singular matrices.
    /// This is a fast and direct solver.
    /// </summary>
    public sealed class LuDecompositionSolver : INumericSolver
    {
        public Vector<double> SolveLinearSystem(Matrix<double> A, Vector<double> b)
        {
            if (A == null)
                throw new ArgumentNullException(nameof(A));
            if (b == null)
                throw new ArgumentNullException(nameof(b));

            if (A.RowCount != b.Count)
                throw new ArgumentException("Matrix and vector dimensions do not agree.");
            if (A.RowCount != A.ColumnCount)
                throw new ArgumentException("LU decomposition requires a square matrix.");

            if (A.Enumerate().Any(d => double.IsNaN(d) || double.IsInfinity(d)) ||
                b.Enumerate().Any(d => double.IsNaN(d) || double.IsInfinity(d)))
                throw new InvalidOperationException("System contains invalid entries.");

            var lu = A.LU();
            if (lu.Determinant == 0.0)
                throw new InvalidOperationException("Matrix is singular or nearly so.");

            return lu.Solve(b);
        }
    }
}
