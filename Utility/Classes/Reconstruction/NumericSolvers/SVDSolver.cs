using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    /// <summary>
    /// Solves Ax = b using Singular Value Decomposition (SVD).
    /// Provides a robust minimum-norm solution for non-square or ill-conditioned matrices.
    /// </summary>
    public sealed class SVDSolver : INumericSolver
    {
        public Vector<double> SolveLinearSystem(Matrix<double> A, Vector<double> b)
        {
            if (A == null)
                throw new ArgumentNullException(nameof(A));
            if (b == null)
                throw new ArgumentNullException(nameof(b));

            if (A.RowCount != b.Count)
                throw new ArgumentException("Matrix and vector dimensions do not agree.");

            if (A.Enumerate().Any(d => double.IsNaN(d) || double.IsInfinity(d)) ||
                b.Enumerate().Any(d => double.IsNaN(d) || double.IsInfinity(d)))
                throw new InvalidOperationException("SVD solver received non-finite entries. This typically indicates a degenerate FEM element in the mesh assembly.");

            var svd = A.Svd(true);
            return svd.Solve(b);
        }
    }
}
