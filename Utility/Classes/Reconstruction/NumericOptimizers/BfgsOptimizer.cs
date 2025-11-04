using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericOptimizers
{
    /// <summary>
    /// BFGS quasi-Newton optimizer. Maintains an inverse Hessian approximation H⁻¹
    /// and applies σ_{k+1} = σ_k - α H⁻¹ ∇J.
    /// </summary>
    public sealed class BfgsOptimizer : INumericOptimizer
    {
        private const double _minConductivity = 1e-6;
        private const double _maxConductivity = 10.0;
        private const double _updateEpsilon = 1e-12;

        private List<int>? _elementOrder;
        private double[,]? _inverseHessian;
        private double[]? _lastSigmaVector;
        private double[]? _lastGradientVector;

        public ConductivityDistribution OptimizationStep(
            ConductivityDistribution currentSigma,
            ConductivityDistribution totalGradient,
            double stepSize)
        {
            EnsureStructure(currentSigma);

            if (_elementOrder == null || _inverseHessian == null)
            {
                return new ConductivityDistribution(currentSigma.Conductivities);
            }

            var sigmaVector = BuildVector(currentSigma);
            var gradientVector = BuildVector(totalGradient);

            UpdateInverseHessianApproximation(sigmaVector, gradientVector);

            var searchDirection = MultiplyMatrixVector(_inverseHessian, gradientVector);
            var next = new Dictionary<int, double>(currentSigma.Conductivities.Count);

            for (int i = 0; i < _elementOrder.Count; i++)
            {
                int id = _elementOrder[i];
                double conductivity = sigmaVector[i];

                double rawStep = -stepSize * searchDirection[i];
                double candidate = conductivity + rawStep;

                candidate = NumericOptimizerGuards.ClipExcessiveGrowth(conductivity, candidate);
                candidate = Math.Max(_minConductivity, Math.Min(_maxConductivity, candidate));

                if (!double.IsFinite(candidate))
                {
                    candidate = conductivity;
                }

                next[id] = candidate;
            }

            _lastSigmaVector = sigmaVector;
            _lastGradientVector = gradientVector;

            return new ConductivityDistribution(next);
        }

        private void EnsureStructure(ConductivityDistribution sigma)
        {
            bool requiresReset = _elementOrder == null
                || _elementOrder.Count != sigma.Conductivities.Count
                || !_elementOrder.All(sigma.Conductivities.ContainsKey);

            if (requiresReset)
            {
                _elementOrder = sigma.Conductivities.Keys.OrderBy(id => id).ToList();
                ResetInverseHessian();
                _lastSigmaVector = null;
                _lastGradientVector = null;
                return;
            }

            if (_elementOrder == null)
                throw new NullReferenceException();

            if (_inverseHessian == null || _inverseHessian.GetLength(0) != _elementOrder.Count)
            {
                ResetInverseHessian();
            }
        }

        private void ResetInverseHessian()
        {
            if (_elementOrder == null)
            {
                _inverseHessian = null;
                return;
            }

            int n = _elementOrder.Count;
            _inverseHessian = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                _inverseHessian[i, i] = 1.0;
            }
        }

        private double[] BuildVector(ConductivityDistribution distribution)
        {
            if (_elementOrder == null)
            {
                return Array.Empty<double>();
            }

            var vector = new double[_elementOrder.Count];
            for (int i = 0; i < _elementOrder.Count; i++)
            {
                int id = _elementOrder[i];
                vector[i] = distribution.GetConductivity(id);
            }

            return vector;
        }

        private void UpdateInverseHessianApproximation(double[] sigmaVector, double[] gradientVector)
        {
            if (_inverseHessian == null || _elementOrder == null)
            {
                return;
            }

            if (_lastSigmaVector == null || _lastGradientVector == null)
            {
                return;
            }

            var s = Subtract(sigmaVector, _lastSigmaVector);
            var y = Subtract(gradientVector, _lastGradientVector);

            double ys = Dot(y, s);
            double yy = Dot(y, y);
            double ss = Dot(s, s);

            if (!double.IsFinite(ys) || !double.IsFinite(yy) || !double.IsFinite(ss))
            {
                ResetInverseHessian();
                return;
            }

            if (ss < _updateEpsilon || yy < _updateEpsilon || Math.Abs(ys) < _updateEpsilon)
            {
                return;
            }

            if (ys <= 0)
            {
                ResetInverseHessian();
                return;
            }

            double rho = 1.0 / ys;
            var currentH = _inverseHessian;
            var Hy = MultiplyMatrixVector(currentH, y);
            double yHy = Dot(y, Hy);

            if (!double.IsFinite(rho) || !double.IsFinite(yHy))
            {
                ResetInverseHessian();
                return;
            }

            int n = _elementOrder.Count;
            var updated = new double[n, n];

            double scale = (1.0 + rho * yHy) * rho;

            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double hij = currentH[i, j];
                    double term1 = rho * Hy[i] * s[j];
                    double term2 = rho * s[i] * Hy[j];
                    double term3 = scale * s[i] * s[j];

                    double candidate = hij - term1 - term2 + term3;
                    if (!double.IsFinite(candidate))
                    {
                        candidate = hij;
                    }

                    updated[i, j] = candidate;
                }
            }

            _inverseHessian = updated;
        }

        private static double[] Subtract(double[] first, double[] second)
        {
            int n = Math.Min(first.Length, second.Length);
            var result = new double[n];
            for (int i = 0; i < n; i++)
            {
                result[i] = first[i] - second[i];
            }

            return result;
        }

        private static double Dot(double[] first, double[] second)
        {
            int n = Math.Min(first.Length, second.Length);
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                sum += first[i] * second[i];
            }

            return sum;
        }

        private static double[] MultiplyMatrixVector(double[,] matrix, double[] vector)
        {
            int rows = matrix.GetLength(0);
            int cols = matrix.GetLength(1);
            var result = new double[rows];

            for (int i = 0; i < rows; i++)
            {
                double sum = 0.0;
                for (int j = 0; j < cols && j < vector.Length; j++)
                {
                    sum += matrix[i, j] * vector[j];
                }

                result[i] = sum;
            }

            return result;
        }
    }
}
