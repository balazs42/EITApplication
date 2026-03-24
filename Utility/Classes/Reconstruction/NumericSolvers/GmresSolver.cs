using System;
using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.ReconstructionParameters;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace Utility.Classes.Reconstruction.NumericSolvers
{
    internal enum GmresVectorExecutionMode
    {
        Cpu,
        Cuda
    }

    /// <summary>
    /// Solves Ax = b using the Generalized Minimal Residual method.
    /// The implementation keeps the Krylov basis in raw arrays to avoid
    /// repeated MathNet vector-expression allocations in the Arnoldi loop.
    /// </summary>
    public sealed class GmresSolver : INumericSolver
    {
        private const double Tolerance = 1e-10;
        private const double HappyBreakdownTolerance = 1e-14;
        internal const int DefaultCudaVectorThresholdDimension = 4096;

        private readonly bool _useCudaAcceleration;
        private GmresVectorExecutionMode _lastVectorExecutionMode = GmresVectorExecutionMode.Cpu;

        internal bool UsesCudaAcceleration => _useCudaAcceleration;
        internal GmresVectorExecutionMode LastVectorExecutionMode => _lastVectorExecutionMode;

        public GmresSolver(bool useCudaAcceleration = false)
        {
            _useCudaAcceleration = useCudaAcceleration;
        }

        internal static GmresVectorExecutionMode SelectVectorExecutionMode(
            bool useCudaAcceleration,
            int systemDimension,
            bool cudaAvailable)
        {
            if (useCudaAcceleration && cudaAvailable && systemDimension >= DefaultCudaVectorThresholdDimension)
                return GmresVectorExecutionMode.Cuda;

            return GmresVectorExecutionMode.Cpu;
        }

        public Vector SolveLinearSystem(Matrix A, Vector b)
        {
            if (A == null)
                throw new ArgumentNullException(nameof(A));
            if (b == null)
                throw new ArgumentNullException(nameof(b));
            if (A.RowCount != b.Count)
                throw new ArgumentException("Matrix and vector dimensions do not agree.");

            int n = b.Count;
            if (n == 0)
            {
                _lastVectorExecutionMode = GmresVectorExecutionMode.Cpu;
                return DenseVector.OfArray(Array.Empty<double>());
            }

            bool cudaAvailable = _useCudaAcceleration && GmresCudaContext.IsAvailable;
            var selectedMode = SelectVectorExecutionMode(_useCudaAcceleration, n, cudaAvailable);

            if (selectedMode == GmresVectorExecutionMode.Cuda &&
                GmresCudaContext.TryCreateSession(n, out var session) &&
                session != null)
            {
                using (session)
                {
                    _lastVectorExecutionMode = GmresVectorExecutionMode.Cuda;
                    return SolveLinearSystemCuda(A, b, n, session);
                }
            }

            _lastVectorExecutionMode = GmresVectorExecutionMode.Cpu;
            return SolveLinearSystemCpu(A, b, n);
        }

        private static Vector SolveLinearSystemCpu(
            Matrix A,
            Vector b,
            int n)
        {
            int maxIter = Math.Min(1000, n);
            double[] residual = b.ToArray();
            double beta = ComputeNorm(residual);

            if (!double.IsFinite(beta))
                throw new InvalidOperationException("Right-hand side contains invalid entries.");

            if (beta < Tolerance)
                return DenseVector.OfArray(new double[n]);

            var basis = new double[maxIter + 1][];
            basis[0] = new double[n];
            for (int i = 0; i < n; i++)
                basis[0][i] = residual[i] / beta;

            var hessenberg = new double[maxIter + 1][];
            for (int i = 0; i < hessenberg.Length; i++)
                hessenberg[i] = new double[maxIter];

            var g = new double[maxIter + 1];
            g[0] = beta;

            var c = new double[maxIter];
            var s = new double[maxIter];

            int basisCount = 0;
            for (int k = 0; k < maxIter; k++)
            {
                double[] w = (A * DenseVector.OfArray(basis[k])).ToArray();

                for (int j = 0; j <= k; j++)
                {
                    double dot = Dot(w, basis[j]);
                    hessenberg[j][k] = dot;
                    Axpy(w, basis[j], -dot);
                }

                double wNorm = ComputeNorm(w);
                if (!double.IsFinite(wNorm))
                    throw new InvalidOperationException("GMRES produced an invalid Krylov vector.");

                hessenberg[k + 1][k] = wNorm;
                basisCount = k + 1;

                if (wNorm < HappyBreakdownTolerance)
                    break;

                basis[k + 1] = new double[n];
                for (int i = 0; i < n; i++)
                    basis[k + 1][i] = w[i] / wNorm;

                for (int j = 0; j < k; j++)
                {
                    double temp = c[j] * hessenberg[j][k] + s[j] * hessenberg[j + 1][k];
                    hessenberg[j + 1][k] = -s[j] * hessenberg[j][k] + c[j] * hessenberg[j + 1][k];
                    hessenberg[j][k] = temp;
                }

                double rho = Math.Sqrt(hessenberg[k][k] * hessenberg[k][k] + hessenberg[k + 1][k] * hessenberg[k + 1][k]);
                if (rho < 1e-30)
                    break;

                c[k] = hessenberg[k][k] / rho;
                s[k] = hessenberg[k + 1][k] / rho;
                hessenberg[k][k] = rho;
                hessenberg[k + 1][k] = 0.0;

                g[k + 1] = -s[k] * g[k];
                g[k] = c[k] * g[k];

                if (Math.Abs(g[k + 1]) < Tolerance)
                {
                    basisCount = k + 1;
                    break;
                }
            }

            if (basisCount == 0)
                basisCount = 1;

            var y = new double[basisCount];
            for (int i = basisCount - 1; i >= 0; i--)
            {
                double sum = g[i];
                for (int j = i + 1; j < basisCount; j++)
                    sum -= hessenberg[i][j] * y[j];

                y[i] = sum / hessenberg[i][i];
            }

            var x = new double[n];
            for (int j = 0; j < basisCount; j++)
                Axpy(x, basis[j], y[j]);

            return DenseVector.OfArray(x);
        }

        private static Vector SolveLinearSystemCuda(Matrix A, Vector b, int n, GmresCudaSession session)
        {
            int maxIter = Math.Min(1000, n);
            double[] residual = b.ToArray();
            session.UploadWorkingVector(residual);
            double beta = session.ComputeWorkingNorm();

            if (!double.IsFinite(beta))
                throw new InvalidOperationException("Right-hand side contains invalid entries.");

            if (beta < Tolerance)
                return DenseVector.OfArray(new double[n]);

            var basis = new double[maxIter + 1][];
            basis[0] = session.ScaleWorkingToArray(1.0 / beta);

            var hessenberg = new double[maxIter + 1][];
            for (int i = 0; i < hessenberg.Length; i++)
                hessenberg[i] = new double[maxIter];

            var g = new double[maxIter + 1];
            g[0] = beta;

            var c = new double[maxIter];
            var s = new double[maxIter];

            int basisCount = 0;
            for (int k = 0; k < maxIter; k++)
            {
                double[] w = (A * DenseVector.OfArray(basis[k])).ToArray();
                session.UploadWorkingVector(w);

                for (int j = 0; j <= k; j++)
                {
                    double dot = session.DotAndSubtractFromWorking(basis[j]);
                    hessenberg[j][k] = dot;
                }

                double wNorm = session.ComputeWorkingNorm();
                if (!double.IsFinite(wNorm))
                    throw new InvalidOperationException("GMRES produced an invalid Krylov vector.");

                hessenberg[k + 1][k] = wNorm;
                basisCount = k + 1;

                if (wNorm < HappyBreakdownTolerance)
                    break;

                basis[k + 1] = session.ScaleWorkingToArray(1.0 / wNorm);

                for (int j = 0; j < k; j++)
                {
                    double temp = c[j] * hessenberg[j][k] + s[j] * hessenberg[j + 1][k];
                    hessenberg[j + 1][k] = -s[j] * hessenberg[j][k] + c[j] * hessenberg[j + 1][k];
                    hessenberg[j][k] = temp;
                }

                double rho = Math.Sqrt(hessenberg[k][k] * hessenberg[k][k] + hessenberg[k + 1][k] * hessenberg[k + 1][k]);
                if (rho < 1e-30)
                    break;

                c[k] = hessenberg[k][k] / rho;
                s[k] = hessenberg[k + 1][k] / rho;
                hessenberg[k][k] = rho;
                hessenberg[k + 1][k] = 0.0;

                g[k + 1] = -s[k] * g[k];
                g[k] = c[k] * g[k];

                if (Math.Abs(g[k + 1]) < Tolerance)
                {
                    basisCount = k + 1;
                    break;
                }
            }

            if (basisCount == 0)
                basisCount = 1;

            var y = new double[basisCount];
            for (int i = basisCount - 1; i >= 0; i--)
            {
                double sum = g[i];
                for (int j = i + 1; j < basisCount; j++)
                    sum -= hessenberg[i][j] * y[j];

                y[i] = sum / hessenberg[i][i];
            }

            session.ResetSolution();
            for (int j = 0; j < basisCount; j++)
                session.AxpySolution(basis[j], y[j]);

            return DenseVector.OfArray(session.DownloadSolution());
        }

        private static void Axpy(double[] target, double[] vector, double scale)
        {
            for (int i = 0; i < target.Length; i++)
                target[i] += scale * vector[i];
        }

        private static double Dot(double[] left, double[] right)
        {
            double sum = 0.0;
            for (int i = 0; i < left.Length; i++)
                sum += left[i] * right[i];
            return sum;
        }

        private static double ComputeNorm(double[] vector)
        {
            return Math.Sqrt(Dot(vector, vector));
        }
    }
}
