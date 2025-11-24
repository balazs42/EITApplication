using System;
using System.Collections.Generic;

namespace Utility.Classes.Reconstruction.ErrorMetrics
{
    /// <summary>
    /// Utility helpers to build Wasserstein ground costs based on arc-length
    /// distances along the closed electrode curve instead of raw Euclidean
    /// distances.
    /// </summary>
    internal static class ArcLengthGroundCostHelper
    {
        private const double Tiny = 1e-12;

        /// <summary>
        /// Builds a squared-distance ground cost matrix using arc-length
        /// distances measured along the closed curve defined by
        /// <paramref name="coords"/>.
        /// </summary>
        public static double[,] BuildArcLengthCost((double x, double y)[] coords)
        {
            var (parameters, totalLength) = BuildArcLengthParameterization(coords);
            return BuildArcLengthCost(parameters, totalLength);
        }

        /// <summary>
        /// Builds the cumulative arc-length parameterization for the provided
        /// coordinates. The first coordinate has parameter 0 and the perimeter
        /// wraps so that the final edge closes the curve.
        /// </summary>
        public static (double[] Parameters, double TotalLength) BuildArcLengthParameterization((double x, double y)[] coords)
        {
            int n = coords.Length;
            if (n == 0)
                return (Array.Empty<double>(), 0.0);

            var parameters = new double[n];
            double total = 0.0;

            for (int i = 1; i < n; i++)
            {
                total += Distance(coords[i - 1], coords[i]);
                parameters[i] = total;
            }

            if (n > 1)
                total += Distance(coords[n - 1], coords[0]);

            return (parameters, total);
        }

        /// <summary>
        /// Builds a squared arc-length ground cost matrix from a precomputed
        /// parameterization and total perimeter length.
        /// </summary>
        public static double[,] BuildArcLengthCost(IReadOnlyList<double> parameters, double totalLength)
        {
            int n = parameters.Count;
            var matrix = new double[n, n];
            if (n == 0 || totalLength <= Tiny)
                return matrix;

            for (int i = 0; i < n; i++)
            {
                matrix[i, i] = 0.0;
                for (int j = i + 1; j < n; j++)
                {
                    double diff = Math.Abs(parameters[i] - parameters[j]);
                    double distance = Math.Min(diff, totalLength - diff);
                    double value = distance * distance;
                    matrix[i, j] = value;
                    matrix[j, i] = value;
                }
            }

            return matrix;
        }

        /// <summary>
        /// Extracts a submatrix from a full cost matrix according to the
        /// provided indices. This allows reusing a cached all-electrode matrix
        /// for arbitrary subsets without recomputation.
        /// </summary>
        public static double[,] SliceCostMatrix(double[,] fullCost, IReadOnlyList<int> indices)
        {
            int n = indices.Count;
            var result = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                int ii = indices[i];
                for (int j = 0; j < n; j++)
                {
                    int jj = indices[j];
                    result[i, j] = fullCost[ii, jj];
                }
            }
            return result;
        }

        private static double Distance((double x, double y) a, (double x, double y) b)
        {
            double dx = a.x - b.x;
            double dy = a.y - b.y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
