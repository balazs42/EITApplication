using Utility.Classes;
using Utility.Classes.ReconstructionParameters;

namespace BusinessLayer
{
    public interface IReconstructionPersistence
    {
        event EventHandler<ReconstructionResult> ReconstructionUpdated;
        event EventHandler<ReconstructionFrame> ReconstructionFrameUpdated;

        bool IsInitialized { get; }
        IReadOnlyList<ReconstructionResult> ReconstructionResults { get; }
        IDifferentialEquationSolver? DifferentialEquationSolver { get; }

        void ResetResults();
    }
}
