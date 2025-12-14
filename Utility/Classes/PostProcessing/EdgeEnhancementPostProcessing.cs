using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class EdgeEnhancementPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Edge Refinement";
        public string Description => "Accentuates conductivity differences along element boundaries.";

        public double Weight { get; set; } = 0.3;

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
                double laplacian = kvp.Value - neighborAvg;
                result[kvp.Key] = kvp.Value + laplacian * Weight;
            }

            return new ConductivityDistribution(result);
        }
    }
}
