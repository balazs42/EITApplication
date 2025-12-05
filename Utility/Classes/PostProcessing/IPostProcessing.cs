using Utility.Classes.Discretizer;

namespace Utility.Classes.PostProcessing
{
    public interface IPostProcessing
    {
        ConductivityDistribution Process(IDiscretization discretization);
    }
}
