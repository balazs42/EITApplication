using System.Collections.Generic;
using Utility.Classes;
using Utility.Classes.ReconstructionParameters;

namespace DataAccessLayer
{
    public interface IReconstructionRepository
    {
        void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters);
        IEnumerable<ReconstructionInfo> GetReconstructions();
        List<ReconstructionResult> LoadReconstruction(string filePath);
    }
}
