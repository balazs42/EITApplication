using System;
using Utility.Classes.ReconstructionParameters;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    /// <summary>
    /// Solves Ax = b using the Conjugate Gradient (CG) method with a diagonal preconditioner.
    /// CG is extremely efficient for large, sparse, symmetric positive definite systems
    /// that frequently arise from FEM discretisations.
    /// </summary>
    public sealed class ConjugateGradientSolver : INumericSolver
    {
        public Vector SolveLinearSystem(Matrix A, Vector b)
        {
            if (A == null)
                throw new ArgumentNullException(nameof(A));
            if (b == null)
                throw new ArgumentNullException(nameof(b));

            if (A.RowCount != b.Count)
                throw new ArgumentException("Matrix and vector dimensions do not agree.");
            if (A.RowCount != A.ColumnCount)
                throw new ArgumentException("Conjugate Gradient requires a square matrix.");

            int n = b.Count;
            const double tolerance = 1e-10;
            int maxIterations = Math.Min(5 * n, 5000);

            var x = Vector.Build.Dense(n, 0.0);
            var r = b - A * x;

            if (r.L2Norm() < tolerance)
                return x;

            var diagonal = new double[n];
            for (int i = 0; i < n; i++)
            {
                double d = A[i, i];
                if (Math.Abs(d) < 1e-14)
                    throw new InvalidOperationException("Matrix diagonal contains zeros making Jacobi preconditioning impossible.");
                diagonal[i] = d;
            }

            Vector Precondition(Vector residual)
            {
                var z = Vector.Build.Dense(residual.Count);
                for (int i = 0; i < residual.Count; i++)
                    z[i] = residual[i] / diagonal[i];
                return z;
            }

            var z0 = Precondition(r);
            var p = z0.Clone();
            double rzOld = r.DotProduct(z0);

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                var Ap = A * p;
                double alphaDenominator = p.DotProduct(Ap);
                if (Math.Abs(alphaDenominator) < 1e-20)
                    throw new InvalidOperationException("Breakdown in conjugate gradient solver: direction became orthogonal.");

                double alpha = rzOld / alphaDenominator;
                x += alpha * p;
                r -= alpha * Ap;

                double residualNorm = r.L2Norm();
                if (residualNorm < tolerance)
                    return x;

                var z = Precondition(r);
                double rzNew = r.DotProduct(z);

                double beta = rzNew / rzOld;
                p = z + beta * p;
                rzOld = rzNew;
            }

            throw new InvalidOperationException("Conjugate Gradient did not converge within the allotted iterations.");
        }
    }
}
