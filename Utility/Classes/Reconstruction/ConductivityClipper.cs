using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Utility.Classes;

namespace Utility.Classes.Reconstruction
{
    public static class ConductivityClipper
    {
        private static readonly object SyncRoot = new();
        private static readonly ParallelOptions DefaultParallelOptions = new();

        private static double _minimumBound = 0.1;
        private static double _maximumBound = 10.0;

        public static double MinimumBound
        {
            get
            {
                lock (SyncRoot)
                {
                    return _minimumBound;
                }
            }
        }

        public static double MaximumBound
        {
            get
            {
                lock (SyncRoot)
                {
                    return _maximumBound;
                }
            }
        }

        public static void UpdateBounds(double minimum, double maximum)
        {
            if (double.IsNaN(minimum) || double.IsInfinity(minimum))
                throw new ArgumentException("Minimum conductivity bound must be a finite number.", nameof(minimum));
            if (double.IsNaN(maximum) || double.IsInfinity(maximum))
                throw new ArgumentException("Maximum conductivity bound must be a finite number.", nameof(maximum));

            if (minimum > maximum)
                (minimum, maximum) = (maximum, minimum);

            lock (SyncRoot)
            {
                _minimumBound = minimum;
                _maximumBound = maximum;
            }
        }

        public static ConductivityDistribution Clip(
            ConductivityDistribution distribution,
            bool useParallel = false,
            ParallelOptions? parallelOptions = null)
        {
            if (distribution == null)
                throw new ArgumentNullException(nameof(distribution));

            Dictionary<int, double> conductivities = distribution.Conductivities;
            if (conductivities.Count == 0)
                return distribution;

            double min;
            double max;

            lock (SyncRoot)
            {
                min = _minimumBound;
                max = _maximumBound;
            }

            bool shouldParallel = (useParallel || parallelOptions != null) && conductivities.Count > 1;
            if (shouldParallel)
            {
                var options = parallelOptions ?? DefaultParallelOptions;
                int length = conductivities.Count;
                var keys = conductivities.Keys.ToArray();
                var sanitizedValues = new double[length];

                Parallel.For(0, length, options, i =>
                {
                    double value = conductivities[keys[i]];
                    sanitizedValues[i] = Sanitize(value, min, max);
                });

                for (int i = 0; i < keys.Length; i++)
                {
                    conductivities[keys[i]] = sanitizedValues[i];
                }
            }
            else
            {
                foreach (var key in conductivities.Keys.ToArray())
                {
                    double value = conductivities[key];
                    conductivities[key] = Sanitize(value, min, max);
                }
            }

            return distribution;
        }

        private static double Sanitize(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value) || double.IsNegativeInfinity(value))
            {
                return minimum;
            }

            if (double.IsPositiveInfinity(value))
            {
                return maximum;
            }

            return Math.Clamp(value, minimum, maximum);
        }
    }
}
