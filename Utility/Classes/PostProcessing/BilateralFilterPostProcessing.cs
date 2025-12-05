using System;
using System.Collections.Generic;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class BilateralFilterPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Bilateral Filter";
        public string Description => "Reduces noise while preserving edges by weighting by similarity.";

        public double Weight { get; set; } = 0.5;

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            var neighbors = PostProcessingHelpers.BuildElementNeighbors(discretization);
            var values = source.Conductivities;
            var result = new Dictionary<int, double>(values.Count);

            double rangeSigma = Math.Max(0.05, Weight);
            double spatialWeight = 0.8; // single hop neighbors only

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

                        double range = kvp.Value - nVal;
                        double similarity = Math.Exp(-(range * range) / (2 * rangeSigma * rangeSigma + 1e-9));
                        double weight = Math.Exp(-1.0 / (2 * spatialWeight * spatialWeight)) * similarity;

                        accum += nVal * weight;
                        totalWeight += weight;
                    }
                }

                result[kvp.Key] = accum / totalWeight;
            }

            return new ConductivityDistribution(result);
        }
    }
}
