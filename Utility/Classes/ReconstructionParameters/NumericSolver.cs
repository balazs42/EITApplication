using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

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

    /// <summary>
    /// Solves Ax = b using Singular Value Decomposition (SVD).
    /// This is a very robust solver that can handle non-square or ill-conditioned matrices
    /// by finding the minimum-norm, least-squares solution.
    /// </summary>
    public sealed class SVDSolver : INumericSolver
    {
        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            Matrix<double> matrixA = DenseMatrix.OfArray(A);
            Vector<double> vectorB = DenseVector.OfArray(b);

            // Perform SVD and solve
            var svd = matrixA.Svd();
            Vector<double> resultX = svd.Solve(vectorB);

            return resultX.ToArray();
        }
    }

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

    /// <summary>
    /// Solves Ax = b using the Generalized Minimal RESidual (GMRES) method.
    /// This is an iterative solver, excellent for very large and sparse systems
    /// that are common in FEM/LBM.
    /// </summary>
    public sealed class GmresSolver : INumericSolver
    {
        public double[] SolveLinearSystem(double[,] A, double[] b)
        {
            if (A.Cast<double>().Any(d => double.IsNaN(d) || double.IsInfinity(d)) ||
                                    b.Any(d => double.IsNaN(d) || double.IsInfinity(d)))
                throw new InvalidOperationException("System contains invalid entries.");

            // Convert input arrays to MathNet types
            Matrix<double> M = DenseMatrix.OfArray(A);
            Vector<double> rhs = DenseVector.OfArray(b);

            int n = rhs.Count;
            int maxIter = Math.Min(1000, n);   // limit iterations to keep memory bounded
            double tol = 1e-10;

            // Initial guess is zero vector
            Vector<double> x = DenseVector.Create(n, 0.0);

            // Initial residual
            Vector<double> r = rhs - M * x;
            double beta = r.L2Norm();

            if (beta < tol)
                return x.ToArray();

            var V = new List<Vector<double>> { r / beta };
            Matrix<double> H = DenseMatrix.Create(maxIter + 1, maxIter, 0.0);
            Vector<double> g = DenseVector.Create(maxIter + 1, 0.0);
            g[0] = beta;

            double[] c = new double[maxIter];
            double[] s = new double[maxIter];

            int k;
            for (k = 0; k < maxIter; k++)
            {
                Vector<double> w = M * V[k];
                for (int j = 0; j <= k; j++)
                {
                    H[j, k] = w.DotProduct(V[j]);
                    w -= H[j, k] * V[j];
                }

                H[k + 1, k] = w.L2Norm();
                if (H[k + 1, k] < 1e-14) // happy breakdown
                    break;

                V.Add(w / H[k + 1, k]);

                // Apply existing Givens rotations
                for (int j = 0; j < k; j++)
                {
                    double temp = c[j] * H[j, k] + s[j] * H[j + 1, k];
                    H[j + 1, k] = -s[j] * H[j, k] + c[j] * H[j + 1, k];
                    H[j, k] = temp;
                }

                // Create new Givens rotation
                double rho = Math.Sqrt(H[k, k] * H[k, k] + H[k + 1, k] * H[k + 1, k]);
                c[k] = H[k, k] / rho;
                s[k] = H[k + 1, k] / rho;
                H[k, k] = rho;
                H[k + 1, k] = 0.0;

                g[k + 1] = -s[k] * g[k];
                g[k] = c[k] * g[k];

                if (Math.Abs(g[k + 1]) < tol)
                {
                    k++;
                    break;
                }
            }

            // Solve upper triangular system H*y = g
            var y = DenseVector.Create(k, 0.0);
            for (int i = k - 1; i >= 0; i--)
            {
                double sum = g[i];
                for (int j = i + 1; j < k; j++)
                    sum -= H[i, j] * y[j];
                y[i] = sum / H[i, i];
            }

            // Reconstruct solution x = V*y
            for (int j = 0; j < k; j++)
                x += V[j] * y[j];

            return x.ToArray();
        }
    }
}