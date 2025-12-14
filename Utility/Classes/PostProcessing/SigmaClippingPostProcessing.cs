using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class SigmaClippingPostProcessing : IPostProcessing
    {
        public string Name => "Outlier Clipping";
        public string Description => "Clamps conductivities outside a 2σ envelope around the mean.";

        public double Sigma { get; set; } = 2.0;

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            if (source.Conductivities.Count == 0)
                return source;

            var values = source.Conductivities.Values.ToList();
            double mean = values.Average();
            double variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
            double stdDev = Math.Sqrt(variance);

            double lower = mean - Sigma * stdDev;
            double upper = mean + Sigma * stdDev;

            var clipped = source.Conductivities.ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, lower, upper));
            return new ConductivityDistribution(clipped);
        }
    }
}
