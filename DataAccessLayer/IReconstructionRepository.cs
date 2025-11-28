using Utility.Classes;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;

namespace DataAccessLayer
{
    public interface IReconstructionRepository
    {
        void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters);
        IEnumerable<ReconstructionInfo> GetReconstructions();
        List<ReconstructionResult> LoadReconstruction(string filePath);
    }
}
