using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public interface IPostProcessing
    {
        string Name { get; }
        string Description { get; }

        ConductivityDistribution Process(IDiscretization discretization, ConductivityDistribution source);
    }

    public interface IWeightedPostProcessing : IPostProcessing
    {
        /// <summary>
        /// Optional weighting/intensity hint provided by the UI (0.0 - 1.0).
        /// </summary>
        double Weight { get; set; }
    }
}
