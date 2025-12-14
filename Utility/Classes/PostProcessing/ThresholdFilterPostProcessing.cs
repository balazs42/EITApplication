using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class ThresholdFilterPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Low-Pass Filter";
        public string Description => "Suppresses conductivities below a weighted threshold.";

        public double Weight { get; set; } = 0.15;

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            if (source.Conductivities.Count == 0)
                return source;

            double min = source.Conductivities.Values.Min();
            double max = source.Conductivities.Values.Max();
            double threshold = min + (max - min) * Weight;

            var filtered = source.Conductivities.ToDictionary(kv => kv.Key, kv => kv.Value < threshold ? threshold : kv.Value);
            return new ConductivityDistribution(filtered);
        }
    }
}
