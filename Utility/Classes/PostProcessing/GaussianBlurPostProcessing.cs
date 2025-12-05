using System;
using System.Collections.Generic;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class GaussianBlurPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Gaussian Blur";
        public string Description => "Smooths conductivity using a soft Gaussian-weighted neighborhood.";

        public double Weight { get; set; } = 0.45;

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            var neighbors = PostProcessingHelpers.BuildElementNeighbors(discretization);
            var values = source.Conductivities;
            var result = new Dictionary<int, double>(values.Count);

            double sigma = Math.Clamp(Weight, 0.05, 1.0);
            double neighborWeight = Math.Exp(-1.0 / (2 * sigma * sigma));

            foreach (var kvp in values)
            {
                double accum = kvp.Value;
                double totalWeight = 1.0;

                if (neighbors.TryGetValue(kvp.Key, out var adjacent))
                {
                    foreach (var id in adjacent)
                    {
                        if (!values.TryGetValue(id, out var nVal))
                            continue;

                        accum += nVal * neighborWeight;
                        totalWeight += neighborWeight;
                    }
                }

                result[kvp.Key] = accum / totalWeight;
            }

            return new ConductivityDistribution(result);
        }
    }
}
