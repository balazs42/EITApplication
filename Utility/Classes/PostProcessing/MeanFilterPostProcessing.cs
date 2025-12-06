using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class MeanFilterPostProcessing : IPostProcessing
    {
        public string Name => "Neighborhood Averaging";
        public string Description => "Smooths the conductivity field by replacing each cell with the mean of its neighbors.";

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

                double sum = kvp.Value;
                foreach (var id in adjacent)
                    sum += values.TryGetValue(id, out var val) ? val : kvp.Value;

                result[kvp.Key] = sum / (adjacent.Count + 1);
            }

            return new ConductivityDistribution(result);
        }
    }
}
