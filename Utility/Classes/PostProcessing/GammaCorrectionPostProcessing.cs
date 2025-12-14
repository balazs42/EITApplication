using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class GammaCorrectionPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Gamma Correction";
        public string Description => "Adjusts brightness by applying a gamma curve to conductivity values.";

        public double Weight { get; set; } = 0.5;

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            var values = source.Conductivities;
            if (values.Count == 0)
                return source;

            double min = values.Values.Min();
            double max = values.Values.Max();
            double range = Math.Max(1e-9, max - min);
            double gamma = 0.5 + Weight * 2.0; // 0.5 .. 2.5

            var result = new Dictionary<int, double>(values.Count);
            foreach (var kvp in values)
            {
                double norm = Math.Clamp((kvp.Value - min) / range, 0.0, 1.0);
                double adjusted = Math.Pow(norm, gamma);
                result[kvp.Key] = min + adjusted * range;
            }

            return new ConductivityDistribution(result);
        }
    }
}
