using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;

namespace ServiceLayer
{
    public interface IReconstructionService
    {
        void InitializeReconstruction(IDiscretization discretization, ReconstructionRuntimeContext parameters, bool reinit);

        // --- Background reconstruction control ---
        event EventHandler<ReconstructionResult> ReconstructionUpdated;
        event EventHandler<ReconstructionFrame> ReconstructionFrameUpdated;
        void StartBackgroundReconstruction(int maxIterationCount, double stepSize, double regularizationWeight, double excitationAmplitude);
        void PauseBackgroundReconstruction();
        void ResumeBackgroundReconstruction();
        void StopBackgroundReconstruction();
        Task<ReconstructionFrame?> StepReconstructionAsync();

        Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                    double regularizationWeight,
                                                                    double excitationAmplitude);

        // --- Persistence ---
        void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters);
        IEnumerable<ReconstructionInfo> GetReconstructions();
        List<ReconstructionResult> LoadReconstruction(string filePath);
    }
}
