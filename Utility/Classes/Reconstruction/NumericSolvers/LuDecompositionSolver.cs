using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra.Factorization;
using Utility.Classes.ReconstructionParameters;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    public sealed class LuDecompositionSolver : INumericSolver
    {
        private static int _providersInitialized;
        private readonly object _sync = new();

        private Matrix? _cachedMatrix;
        private LU<double>? _cachedFactorization;

        public LuDecompositionSolver()
        {
            InitializeProvidersOnce();
        }

        public Vector SolveLinearSystem(Matrix A, Vector b)
        {
            ArgumentNullException.ThrowIfNull(A);
            ArgumentNullException.ThrowIfNull(b);

            if (A.RowCount != A.ColumnCount)
                throw new ArgumentException("LU requires a square matrix.", nameof(A));
            if (A.RowCount != b.Count)
                throw new ArgumentException("Matrix and vector dimensions do not agree.", nameof(b));

            lock (_sync)
            {
                EnsureFactorized(A);
                return _cachedFactorization!.Solve(b);
            }
        }

        // Use this whenever you have multiple RHS vectors.
        public Matrix SolveLinearSystems(Matrix A, Matrix B)
        {
            ArgumentNullException.ThrowIfNull(A);
            ArgumentNullException.ThrowIfNull(B);

            if (A.RowCount != A.ColumnCount)
                throw new ArgumentException("LU requires a square matrix.", nameof(A));
            if (A.RowCount != B.RowCount)
                throw new ArgumentException("Matrix and RHS dimensions do not agree.", nameof(B));

            lock (_sync)
            {
                EnsureFactorized(A);
                return _cachedFactorization!.Solve(B);
            }
        }

        public void InvalidateCache()
        {
            lock (_sync)
            {
                _cachedMatrix = null;
                _cachedFactorization = null;
            }
        }

        private void EnsureFactorized(Matrix A)
        {
            // IMPORTANT:
            // This assumes A is immutable while cached.
            // If A can be modified in place, call InvalidateCache() before reuse.
            if (!ReferenceEquals(_cachedMatrix, A) || _cachedFactorization is null)
            {
                _cachedFactorization = A.LU();
                _cachedMatrix = A;
            }
        }

        private static void InitializeProvidersOnce()
        {
            if (Interlocked.Exchange(ref _providersInitialized, 1) != 0)
                return;

            // Lowest-friction acceleration path inside Math.NET:
            // try CUDA first, then MKL, then OpenBLAS.
            if (!Control.TryUseNativeCUDA())
            {
                if (!Control.TryUseNativeMKL())
                {
                    Control.TryUseNativeOpenBLAS();
                }
            }

            Control.UseMultiThreading();
        }
    }
}