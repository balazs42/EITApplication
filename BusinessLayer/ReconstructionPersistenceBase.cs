using Utility.Classes;
using Utility.Classes.ReconstructionParameters;

namespace BusinessLayer
{
    public abstract class ReconstructionPersistenceBase : IReconstructionPersistence
    {
        private readonly List<ReconstructionResult> _reconstructionResults = [];

        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        public abstract bool IsInitialized { get; }
        public abstract IDifferentialEquationSolver? DifferentialEquationSolver { get; }
        public IReadOnlyList<ReconstructionResult> ReconstructionResults => _reconstructionResults;

        public void ResetResults() => _reconstructionResults.Clear();

        protected void PublishFrame(ReconstructionFrame frame)
            => ReconstructionFrameUpdated?.Invoke(this, frame);

        protected ReconstructionResult PublishResult(ReconstructionResult result)
        {
            _reconstructionResults.Add(result);
            ReconstructionUpdated?.Invoke(this, result);
            return result;
        }
    }
}
