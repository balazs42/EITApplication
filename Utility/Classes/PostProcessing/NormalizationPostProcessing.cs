using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public class NormalizationPostProcessing : IPostProcessing
    {
        public string Name => "Normalize Range";
        public string Description => "Scales conductivities into [0,1] while preserving contrast.";

        public ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source)
        {
            if (source.Conductivities.Count == 0)
                return source;

            double min = source.Conductivities.Values.Min();
            double max = source.Conductivities.Values.Max();
            double range = Math.Max(1e-9, max - min);

            var normalized = source.Conductivities.ToDictionary(kv => kv.Key, kv => (kv.Value - min) / range);
            return new ConductivityDistribution(normalized);
        }
    }
}
