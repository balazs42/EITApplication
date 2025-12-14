using System;
using System.Collections.Generic;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class AnisotropicDiffusionPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Anisotropic Diffusion";
        public string Description => "Edge-preserving smoothing that diffuses within regions while respecting strong gradients.";

        public double Weight { get; set; } = 0.4;

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            var neighbors = PostProcessingHelpers.BuildElementNeighbors(discretization);
            var values = source.Conductivities;
            var result = new Dictionary<int, double>(values.Count);
            double sensitivity = Math.Max(0.05, Weight);

            foreach (var kvp in values)
            {
                if (!neighbors.TryGetValue(kvp.Key, out var adjacent) || adjacent.Count == 0)
                {
                    result[kvp.Key] = kvp.Value;
                    continue;
                }

                double center = kvp.Value;
                double update = 0;
                int count = 0;

                foreach (var neighborId in adjacent)
                {
                    var neighbor = values.TryGetValue(neighborId, out var v) ? v : center;
                    double diff = neighbor - center;
                    double conductance = Math.Exp(-(diff * diff) / (2 * sensitivity * sensitivity));
                    update += conductance * diff;
                    count++;
                }

                result[kvp.Key] = center + update / Math.Max(1, count);
            }

            return new ConductivityDistribution(result);
        }
    }
}
