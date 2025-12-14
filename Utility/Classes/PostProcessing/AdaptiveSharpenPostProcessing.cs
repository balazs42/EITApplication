using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class AdaptiveSharpenPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Adaptive Sharpen";
        public string Description => "Boosts local contrast relative to the neighborhood while capping overshoot.";

        public double Weight { get; set; } = 0.35;

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            var neighbors = PostProcessingHelpers.BuildElementNeighbors(discretization);
            var values = source.Conductivities;
            var result = new Dictionary<int, double>(values.Count);

            foreach (var kvp in values)
            {
                if (!neighbors.TryGetValue(kvp.Key, out var adjacent) || adjacent.Count == 0)
                {
                    result[kvp.Key] = kvp.Value;
                    continue;
                }

                double center = kvp.Value;
                double mean = adjacent
                    .Select(id => values.TryGetValue(id, out var v) ? v : center)
                    .DefaultIfEmpty(center)
                    .Average();

                double detail = center - mean;
                double boost = 1.0 + Weight;
                double sharpened = mean + detail * boost;

                // Clamp to avoid runaway highlights/darks
                double minNeighbor = adjacent.Min(id => values.TryGetValue(id, out var v) ? v : center);
                double maxNeighbor = adjacent.Max(id => values.TryGetValue(id, out var v) ? v : center);
                result[kvp.Key] = Math.Clamp(sharpened, minNeighbor - detail, maxNeighbor + detail);
            }

            return new ConductivityDistribution(result);
        }
    }
}
