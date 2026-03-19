using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;

namespace ServiceLayer
{
    public interface IReconstructionService
    {
        event EventHandler<ReconstructionResult> ReconstructionUpdated;
        event EventHandler<ReconstructionFrame> ReconstructionFrameUpdated;

        bool VisualizeIterations { get; set; }
        bool IsInitialized { get; }
        bool IsRunning { get; }
        bool IsPaused { get; }
        IReadOnlyList<ReconstructionResult> ReconstructionResults { get; }

        void InitializeReconstruction(IDiscretization discretization, ReconstructionRuntimeContext parameters, bool reinit);
        void Run(int maxIterationCount, double stepSize, double regularizationWeight, double excitationAmplitude);
        void Pause();
        void Resume();
        void Stop();

        Task<ReconstructionResult?> StepReconstructionAsync(double stepSize,
                                                            double regularizationWeight,
                                                            double excitationAmplitude);

        Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                    double regularizationWeight,
                                                                    double excitationAmplitude);

        void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters);
        IEnumerable<ReconstructionInfo> GetReconstructions();
        List<ReconstructionResult> LoadReconstruction(string filePath);
    }
}
