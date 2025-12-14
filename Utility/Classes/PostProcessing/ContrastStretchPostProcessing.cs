using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class ContrastStretchPostProcessing : IPostProcessing
    {
        public string Name => "Contrast Stretch";
        public string Description => "Expands values between the 5th and 95th percentile for better contrast.";

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            var values = source.Conductivities;
            var ordered = values.Values.OrderBy(v => v).ToList();
            if (ordered.Count == 0)
                return source;

            double low = Percentile(ordered, 0.05);
            double high = Percentile(ordered, 0.95);
            if (high - low < 1e-9)
                return source;

            var min = ordered.First();
            var max = ordered.Last();
            var result = new Dictionary<int, double>(values.Count);

            foreach (var kvp in values)
            {
                double normalized = Math.Clamp((kvp.Value - low) / (high - low), 0.0, 1.0);
                result[kvp.Key] = min + normalized * (max - min);
            }

            return new ConductivityDistribution(result);
        }

        private static double Percentile(IReadOnlyList<double> ordered, double p)
        {
            int idx = (int)Math.Clamp(Math.Round(p * (ordered.Count - 1)), 0, ordered.Count - 1);
            return ordered[idx];
        }
    }
}
