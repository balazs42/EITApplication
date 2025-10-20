using MathNet.Numerics.LinearAlgebra.Double;
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
        /// <param name="threshold">Singular values smaller than this will be ignored (set to zero). 
        /// A common choice is 1e-6, but this should be tuned.</param>
        public tSVDSolver(double threshold = 1e-6)
        {
            _threshold = threshold;
        }

        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            if (A.Cast<double>().Any(d => double.IsNaN(d) || double.IsInfinity(d)) ||
                b.Any(d => double.IsNaN(d) || double.IsInfinity(d)))
                throw new InvalidOperationException("tSVD solver received non-finite entries. Check the FEM mesh for degenerate elements that yield NaN/Inf stiffness coefficients.");

            var M = DenseMatrix.OfArray(A);
            var y = DenseVector.OfArray(b);
            var svd = M.Svd(computeVectors: true);
            var U = svd.U; var VT = svd.VT; var s = svd.S;

            // Truncálás
            for (int i = 0; i < s.Count; i++)
                if (s[i] < _threshold) s[i] = 0.0;

            // x = V Σ⁻¹ Uᵀ b  (ahol Σ⁻¹[i]= 1/s[i] ha s[i]>0, különben 0)
            var UTb = U.TransposeThisAndMultiply(y);

            // Only the first min(m,n) components have corresponding singular values;
            // avoid accessing beyond s.Count for rectangular matrices.
            for (int i = 0; i < s.Count; i++)
                UTb[i] = (s[i] > 0) ? UTb[i] / s[i] : 0.0;

            for (int i = s.Count; i < UTb.Count; i++)
                UTb[i] = 0.0;

            var x = VT.TransposeThisAndMultiply(UTb);
            return x.ToArray();
        }
    }

}
