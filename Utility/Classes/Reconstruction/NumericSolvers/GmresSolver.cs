using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.ReconstructionParameters;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace Utility.Classes.Reconstruction.NumericSolvers
{

    /// <summary>
    /// Solves Ax = b using the Generalized Minimal RESidual (GMRES) method.
    /// This is an iterative solver, excellent for very large and sparse systems
    /// that are common in FEM/LBM.
    /// </summary>
    public sealed class GmresSolver : INumericSolver
    {
        public Vector SolveLinearSystem(Matrix A, Vector b)
        {
            if (A == null)
                throw new ArgumentNullException(nameof(A));
            if (b == null)
                throw new ArgumentNullException(nameof(b));

            if (A.RowCount != b.Count)
                throw new ArgumentException("Matrix and vector dimensions do not agree.");

            if (A.Enumerate().Any(d => double.IsNaN(d) || double.IsInfinity(d)) ||
                b.Enumerate().Any(d => double.IsNaN(d) || double.IsInfinity(d)))
                throw new InvalidOperationException("System contains invalid entries.");

            int n = b.Count;
            int maxIter = Math.Min(1000, n);   // limit iterations to keep memory bounded
            double tol = 1e-10;

            // Initial guess is zero vector
            Vector x = Vector.Build.Dense(n, 0.0);

            // Initial residual
            Vector r = b - A * x;
            double beta = r.L2Norm();

            if (beta < tol)
                return x;

            var V = new List<Vector> { r / beta };
            Matrix H = DenseMatrix.Create(maxIter + 1, maxIter, 0.0);
            Vector g = Vector.Build.Dense(maxIter + 1, 0.0);
            g[0] = beta;

            double[] c = new double[maxIter];
            double[] s = new double[maxIter];

            int k;
            for (k = 0; k < maxIter; k++)
            {
                Vector w = A * V[k];
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
                if (rho < 1e-30)
                    break;

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
            var y = Vector.Build.Dense(k, 0.0);
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

            return x;
        }
    }
}
