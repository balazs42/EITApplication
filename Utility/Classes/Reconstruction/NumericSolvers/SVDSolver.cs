using System;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.ReconstructionParameters;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    /// <summary>
    /// Solves Ax = b using Singular Value Decomposition (SVD).
    /// Provides a robust minimum-norm solution for non-square or ill-conditioned matrices.
    /// </summary>
    public sealed class SVDSolver : INumericSolver
    {
        public Vector SolveLinearSystem(Matrix A, Vector b)
        {
            if (A == null)
                throw new ArgumentNullException(nameof(A));
            if (b == null)
                throw new ArgumentNullException(nameof(b));

            if (A.RowCount != b.Count)
                throw new ArgumentException("Matrix and vector dimensions do not agree.");

            //if (A.Enumerate(Zeros.AllowSkip).Any(d => double.IsNaN(d) || double.IsInfinity(d)) ||
            //    b.Enumerate(Zeros.AllowSkip).Any(d => double.IsNaN(d) || double.IsInfinity(d)))
            //    throw new InvalidOperationException("SVD solver received non-finite entries. This typically indicates a degenerate FEM element in the mesh assembly.");

            var dense = A as DenseMatrix ?? DenseMatrix.OfMatrix(A);

            var svd = dense.Svd(computeVectors: true);
            var singularValues = svd.S;

            if (singularValues.Count == 0)
                throw new InvalidOperationException("SVD failed to compute singular values.");

            double sigmaMax = singularValues[0];
            double tolerance = Math.Max(dense.RowCount, dense.ColumnCount) * Precision.MachineEpsilon * sigmaMax;

            var projected = svd.U.TransposeThisAndMultiply(b);
            var y = Vector.Build.Dense(singularValues.Count);

            for (int i = 0; i < singularValues.Count; i++)
            {
                double sigma = singularValues[i];
                if (sigma > tolerance)
                {
                    y[i] = projected[i] / sigma;
                }
                else
                {
                    y[i] = 0.0;
                }
            }

            return svd.VT.TransposeThisAndMultiply(y);
        }
    }
}
