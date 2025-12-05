using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class LaplacianSmoothingPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Laplacian Smoothing";
        public string Description => "Averages each element with its neighbors to reduce noise.";

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

                double neighborAvg = adjacent.Average(id => values.TryGetValue(id, out var v) ? v : kvp.Value);
                result[kvp.Key] = kvp.Value * (1 - Weight) + neighborAvg * Weight;
            }

            return new ConductivityDistribution(result);
        }
    }
}
