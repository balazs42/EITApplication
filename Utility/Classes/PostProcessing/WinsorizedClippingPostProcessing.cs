using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class WinsorizedClippingPostProcessing : IPostProcessing
    {
        public string Name => "Winsorized Clipping";
        public string Description => "Suppresses extreme outliers by clamping to percentile bounds.";

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            var values = source.Conductivities;
            if (values.Count == 0)
                return source;

            var ordered = values.Values.OrderBy(v => v).ToList();
            double low = Percentile(ordered, 0.02);
            double high = Percentile(ordered, 0.98);

            var result = new Dictionary<int, double>(values.Count);
            foreach (var kvp in values)
            {
                result[kvp.Key] = Math.Clamp(kvp.Value, low, high);
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
