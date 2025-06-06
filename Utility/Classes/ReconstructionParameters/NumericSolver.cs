using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.LinearAlgebra.Double.Solvers;
using MathNet.Numerics.LinearAlgebra.Solvers;

namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericSolver
    {
        LUDecomposition = 1,
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
            // Convert native C# arrays to MathNet types
            Matrix<double> matrixA = DenseMatrix.OfArray(A);
            Vector<double> vectorB = DenseVector.OfArray(b);

            if (matrixA.RowCount != matrixA.ColumnCount)
                throw new ArgumentException("LU decomposition requires a square matrix.");

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
            Matrix<double> matrixA = DenseMatrix.OfArray(A);
            Vector<double> vectorB = DenseVector.OfArray(b);

            // Perform SVD to get the components U, S, and V
            var svd = matrixA.Svd();
            Matrix<double> U = svd.U;
            Vector<double> s = svd.S; // Vector of singular values
            Matrix<double> V = svd.VT;

            // Manually compute the solution x = V * Σ_inv * U^T * b
            // with truncation on the inverse of the singular values.
            var s_inv = s.PointwiseMultiply(s.Map(x => x < _threshold ? 0.0 : 1.0)); // Zero out small values
            s_inv = s_inv.PointwiseDivide(s); // Invert non-zero values
            s_inv.CoerceZero(1e12); // Coerce large values resulting from near-zero division to 0

            var Sigma_inv = DenseMatrix.OfDiagonalVector(s_inv);

            // The dimensions of Sigma_inv must match U^T.
            // SVD factorization A = U*S*V' gives U (m x p), S (p x p diag), V (n x p) where p=min(m,n)
            // We need a (p x m) Σ_inv matrix.
            var correctSigmaInv = new DenseMatrix(V.ColumnCount, U.ColumnCount);
            for (int i = 0; i < Math.Min(V.ColumnCount, U.ColumnCount); ++i)
                correctSigmaInv[i, i] = s_inv[i];


            Vector<double> resultX = V.Multiply(correctSigmaInv.Multiply(U.TransposeThisAndMultiply(vectorB)));

            return resultX.ToArray();
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
            // Note: GMRES is most efficient with sparse matrices. Here we build a dense
            // one as required by the interface, but for production, you might want to
            // build a sparse matrix directly inside your FEM/LBM assembler.
            // Matrix<double> matrixA = DenseMatrix.OfArray(A);
            // Vector<double> vectorB = DenseVector.OfArray(b);

            // Create an iterative solver monitor to control the process if needed
            // (e.g., set maximum iterations, check for convergence)
            //var monitor = new IterationMonitor<double>(
            //    new IterativeSolverStopCriterion<double>[]
            //    {
            //        new FailureStopCriterion<double>(),
            //        new DivergenceStopCriterion<double>(),
            //        new IterationCountStopCriterion<double>(1000),
            //        new ResidualStopCriterion<double>(1e-10)
            //    });

            //new Gmres(monitor);
            // Solve the system
            //Vector<double> resultX = solver.Solve(matrixA, vectorB);
            //
            //return resultX.ToArray();
            throw new NotImplementedException();
        }
    }
}