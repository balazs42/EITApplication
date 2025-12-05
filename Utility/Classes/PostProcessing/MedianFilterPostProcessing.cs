using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class MedianFilterPostProcessing : IPostProcessing
    {
        public string Name => "Median Filter";
        public string Description => "Replaces each element by the median of its neighborhood to suppress spikes.";

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

                var window = new List<double>(adjacent.Count + 1) { kvp.Value };
                foreach (var id in adjacent)
                    window.Add(values.TryGetValue(id, out var val) ? val : kvp.Value);

                window.Sort();
                double median = window[window.Count / 2];
                result[kvp.Key] = median;
            }

            return new ConductivityDistribution(result);
        }
    }
}
