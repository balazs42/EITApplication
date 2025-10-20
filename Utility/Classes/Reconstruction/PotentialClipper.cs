using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Utility.Classes;

namespace Utility.Classes.Reconstruction
{
    public static class PotentialClipper
    {
        private static readonly ParallelOptions DefaultParallelOptions = new();

        public static PotentialDistribution Clip(
            PotentialDistribution distribution,
            bool useParallel = false,
            ParallelOptions? parallelOptions = null)
        {
            if (distribution == null)
                throw new ArgumentNullException(nameof(distribution));

            Dictionary<int, double> potentials = distribution.Potentials;
            if (potentials.Count == 0)
                return distribution;

            bool shouldParallel = (useParallel || parallelOptions != null) && potentials.Count > 1;
            if (shouldParallel)
            {
                var options = parallelOptions ?? DefaultParallelOptions;
                int length = potentials.Count;
                var keys = potentials.Keys.ToArray();
                var sanitizedValues = new double[length];

                Parallel.For(0, length, options, i =>
                {
                    double value = potentials[keys[i]];
                    sanitizedValues[i] = Sanitize(value);
                });

                for (int i = 0; i < keys.Length; i++)
                {
                    potentials[keys[i]] = sanitizedValues[i];
                }
            }
            else
            {
                foreach (var key in potentials.Keys.ToArray())
                {
                    double value = potentials[key];
                    potentials[key] = Sanitize(value);
                }
            }

            return distribution;
        }

        public static double[] Clip(
            double[] potentials,
            bool useParallel = false,
            ParallelOptions? parallelOptions = null)
        {
            if (potentials == null)
                throw new ArgumentNullException(nameof(potentials));

            if (potentials.Length == 0)
                return potentials;

            bool shouldParallel = (useParallel || parallelOptions != null) && potentials.Length > 1;
            if (shouldParallel)
            {
                var options = parallelOptions ?? DefaultParallelOptions;
                Parallel.For(0, potentials.Length, options, i =>
                {
                    double value = potentials[i];
                    potentials[i] = Sanitize(value);
                });
            }
            else
            {
                for (int i = 0; i < potentials.Length; i++)
                {
                    double value = potentials[i];
                    potentials[i] = Sanitize(value);
                }
            }

            return potentials;
        }

        private static double Sanitize(double value)
        {
            return double.IsFinite(value) ? value : 0.0;
        }
    }
}
