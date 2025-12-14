using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class HighPassEnhancementPostProcessing : IWeightedPostProcessing
    {
        public string Name => "Unsharp Mask";
        public string Description => "Sharpens features by combining the original with a blurred subtraction.";

        public double Weight { get; set; } = 0.4;

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            var blur = new GaussianBlurPostProcessing { Weight = 0.6 }.Process(discretization, source);
            var min = source.Conductivities.Values.Min();
            var max = source.Conductivities.Values.Max();
            double range = Math.Max(1e-9, max - min);

            var result = new Dictionary<int, double>(source.Conductivities.Count);
            foreach (var kvp in source.Conductivities)
            {
                double blurred = blur.Conductivities.TryGetValue(kvp.Key, out var b) ? b : kvp.Value;
                double enhanced = kvp.Value + (kvp.Value - blurred) * Weight;
                result[kvp.Key] = Math.Clamp(enhanced, min - 0.1 * range, max + 0.1 * range);
            }

            return new ConductivityDistribution(result);
        }
    }
}
