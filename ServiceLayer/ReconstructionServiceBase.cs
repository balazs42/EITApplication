using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;

namespace ServiceLayer
{
    public abstract class ReconstructionServiceBase : IReconstructionService
    {
        private readonly List<ReconstructionResult> _reconstructionResults = [];
        private CancellationTokenSource? _backgroundCts;
        private Task? _backgroundTask;
        private bool _isPaused;

        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        public bool VisualizeIterations { get; set; } = true;
        public abstract bool IsInitialized { get; }
        public bool IsRunning => _backgroundTask != null && !_backgroundTask.IsCompleted;
        public bool IsPaused => _isPaused;
        public IReadOnlyList<ReconstructionResult> ReconstructionResults => _reconstructionResults;

        public abstract void InitializeReconstruction(IDiscretization discretization, ReconstructionRuntimeContext parameters, bool reinit);

        public void Run(int maxIterationCount, double stepSize, double regularizationWeight, double excitationAmplitude)
        {
            Stop();
            _isPaused = false;
            _backgroundCts = new CancellationTokenSource();
            _backgroundTask = Task.Run(async () =>
            {
                try
                {
                    await RunCoreAsync(maxIterationCount, stepSize, regularizationWeight, excitationAmplitude, _backgroundCts.Token);
                }
                finally
                {
                    _backgroundTask = null;
                    _backgroundCts?.Dispose();
                    _backgroundCts = null;
                    _isPaused = false;
                }
            }, _backgroundCts.Token);
        }

        public void Pause() => _isPaused = true;

        public void Resume() => _isPaused = false;

        public void Stop()
        {
            _backgroundCts?.Cancel();
            _backgroundTask = null;
            _isPaused = false;
        }

        public Task<ReconstructionResult?> StepReconstructionAsync(double stepSize,
                                                                   double regularizationWeight,
                                                                   double excitationAmplitude)
            => StepCoreAsync(stepSize, regularizationWeight, excitationAmplitude);

        public abstract Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                                    double regularizationWeight,
                                                                                    double excitationAmplitude);

        public abstract void SaveReconstruction(List<ReconstructionResult> frames, string name, ReconstructionRuntimeContext parameters);
        public abstract IEnumerable<ReconstructionInfo> GetReconstructions();
        public abstract List<ReconstructionResult> LoadReconstruction(string filePath);

        protected abstract Task RunCoreAsync(int maxIterationCount,
                                             double stepSize,
                                             double regularizationWeight,
                                             double excitationAmplitude,
                                             CancellationToken cancellationToken);

        protected abstract Task<ReconstructionResult?> StepCoreAsync(double stepSize,
                                                                     double regularizationWeight,
                                                                     double excitationAmplitude);

        protected async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
        {
            while (_isPaused && !cancellationToken.IsCancellationRequested)
                await Task.Delay(100, cancellationToken);
        }

        protected void ClearPublishedResults() => _reconstructionResults.Clear();

        protected void PublishFrame(ReconstructionFrame frame)
        {
            if (VisualizeIterations)
                ReconstructionFrameUpdated?.Invoke(this, frame);
        }

        protected ReconstructionResult PublishResult(ReconstructionResult result)
        {
            _reconstructionResults.Add(result);
            ReconstructionUpdated?.Invoke(this, result);
            return result;
        }
    }
}
