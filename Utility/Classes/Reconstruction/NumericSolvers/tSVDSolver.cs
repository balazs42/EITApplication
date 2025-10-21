using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    /// <summary>
    /// Solves Ax = b using a Truncated SVD (tSVD).
    /// This is a form of regularization ideal for very ill-posed inverse problems.
    /// It filters out noise by ignoring the smallest singular values.
    /// </summary>
    public sealed class tSVDSolver : INumericSolver
    {
        private readonly double _threshold;

        /// <summary>
        /// Initializes the solver with a truncation threshold.
        /// </summary>
        /// <param name="threshold">Singular values smaller than this will be ignored (set to zero).</param>
        public tSVDSolver(double threshold = 1e-6)
        {
            _threshold = threshold;
        }

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
                throw new InvalidOperationException("tSVD solver received non-finite entries. Check the FEM mesh for degenerate elements that yield NaN/Inf stiffness coefficients.");

            var svd = A.Svd(true);
            var singularValues = svd.S.Clone();

            for (int i = 0; i < singularValues.Count; i++)
            {
                if (singularValues[i] < _threshold)
                    singularValues[i] = 0.0;
            }

            var UTb = svd.U.TransposeThisAndMultiply(b);

            int count = singularValues.Count;
            for (int i = 0; i < count; i++)
                UTb[i] = singularValues[i] > 0.0 ? UTb[i] / singularValues[i] : 0.0;

            for (int i = count; i < UTb.Count; i++)
                UTb[i] = 0.0;

            return svd.VT.TransposeThisAndMultiply(UTb);
        }
    }
}
