using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.ApplicationModel;
using ServiceLayer;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Timers;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        private readonly Stopwatch _reconstructionStopwatch = new();
        private readonly Timer _elapsedTimer;

        private IDiscretization? _discretization = Workspace.GetDiscretization();
        private IDiscretization? _initializedDiscretization;

        [ObservableProperty]
        private int iterationCount = 0;

        [ObservableProperty]
        private double residual = 1.0;

        [ObservableProperty]
        private TimeSpan elapsedTime = TimeSpan.Zero;

        [ObservableProperty]
        private double correlation = 0.0;

        [ObservableProperty]
        private bool adjecentDrivePattern = true;

        [ObservableProperty]
        private bool oppositeDrivePattern = false;

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string reconstructionSearchText = string.Empty;

        public ObservableCollection<ReconstructionInfo> AvailableReconstructions { get; } = [];
        public ObservableCollection<ReconstructionInfo> FilteredReconstructions { get; } = [];
        public ObservableCollection<double> ResidualHistory { get; } = [];

        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        public ReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            _elapsedTimer = new Timer(200)
            {
                AutoReset = true,
                Enabled = false
            };

            _elapsedTimer.Elapsed += (_, _) =>
            {
                if (!_reconstructionStopwatch.IsRunning)
                    return;

                MainThread.BeginInvokeOnMainThread(() => ElapsedTime = _reconstructionStopwatch.Elapsed);
            };

            // Use global reconstruction parameters stored in the workspace
            ReconstructionParameters = Workspace.GetReconstructionParameters();

            _reconstructionService.ReconstructionUpdated += OnServiceReconstructionUpdated;
            _reconstructionService.ReconstructionFrameUpdated += OnServiceFrameUpdated;
        }

        public void PrepareForNewReconstruction()
        {
            InitializeReconstruction(force: true);
            ResetReconstructionMetrics();
        }

        public void ResetReconstructionMetrics()
        {
            ResidualHistory.Clear();
            Residual = 0.0;
            Correlation = 0.0;
            ElapsedTime = TimeSpan.Zero;
            _reconstructionStopwatch.Reset();
            StopElapsedTimer();
        }

        public void BeginReconstructionMetrics()
        {
            if (!_reconstructionStopwatch.IsRunning)
                _reconstructionStopwatch.Start();

            StartElapsedTimer();
        }

        public void PauseReconstructionMetrics()
        {
            if (_reconstructionStopwatch.IsRunning)
            {
                _reconstructionStopwatch.Stop();
                ElapsedTime = _reconstructionStopwatch.Elapsed;
            }

            StopElapsedTimer();
        }

        public void StopReconstructionMetrics() => PauseReconstructionMetrics();

        private void StartElapsedTimer()
        {
            if (!_elapsedTimer.Enabled)
                _elapsedTimer.Start();
        }

        private void StopElapsedTimer()
        {
            if (_elapsedTimer.Enabled)
                _elapsedTimer.Stop();
        }

        partial void OnReconstructionSearchTextChanged(string value) => ApplyReconstructionFilter();
        private void UpdateMesh() => _discretization = Workspace.GetDiscretization();
        private void UpdateReconstructionParameters() => ReconstructionParameters = Workspace.GetReconstructionParameters();

        private void InitializeReconstruction(bool force = false)
        {
            UpdateMesh();
            UpdateReconstructionParameters();

            var mesh = _discretization;
            var reconstructionParameters = ReconstructionParameters;

            if (mesh == null)
                throw new NullReferenceException("Mesh was null during reconstruction initialization, check calling code!");

            if (force || _initializedDiscretization != mesh)
            {
                _reconstructionService.InitializeReconstruction(mesh, reconstructionParameters, true);
                _initializedDiscretization = mesh;

                IterationCount = 0;
            }
        }

        public bool CheckReconstructionMethodAgainstMesh()
        {
            if(_discretization is FEMMesh)
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FiniteElementMethod)
                    return false;
            else if(_discretization is LBMGrid)
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.LatticeBoltzmannMethod)
                    return false;

            return true;
        }

        public async void OnSolveForwardClicked(object sender, EventArgs e)
        {
            InitializeReconstruction(force: true);

            if (_discretization is FEMMesh)
                await Task.Run(() => _reconstructionService.ForwardSolveStepFem());
            else if (_discretization is LBMGrid)
                await Task.Run(() => _reconstructionService.ForwardSolveStepLbm());
        }

        public async void OnSolveInverseClicked(object sender, EventArgs e)
        {
            InitializeReconstruction(force: true);
            ResetReconstructionMetrics();
            BeginReconstructionMetrics();

            try
            {
                if (_discretization is FEMMesh)
                    await Task.Run(() => _reconstructionService.InverseSolveFem(MaxIterationCount, StepSize, RegularizationWeight, ExcitationCurrentAmplitude));
                else if (_discretization is LBMGrid)
                    await Task.Run(() => _reconstructionService.InverseSolveLbm(MaxIterationCount, StepSize, RegularizationWeight, ExcitationCurrentAmplitude));
            }
            finally
            {
                PauseReconstructionMetrics();
            }
        }

        private double CalculateResidual(ConductivityDistribution reconstructed, ConductivityDistribution original)
        {
            double sum = 0.0;
            foreach (var kv in reconstructed.Conductivities)
            {
                original.Conductivities.TryGetValue(kv.Key, out double origVal);
                double diff = kv.Value - origVal;
                sum += diff * diff;
            }
            return Math.Sqrt(sum);
        }

        private double CalculateCorrelation(ConductivityDistribution reconstructed, ConductivityDistribution original)
        {
            if (reconstructed.Conductivities.Count == 0)
                return 0.0;

            double sumReconstructed = 0.0;
            double sumOriginal = 0.0;
            foreach (var kv in reconstructed.Conductivities)
            {
                sumReconstructed += kv.Value;
                original.Conductivities.TryGetValue(kv.Key, out double origVal);
                sumOriginal += origVal;
            }

            int count = reconstructed.Conductivities.Count;
            double meanReconstructed = sumReconstructed / count;
            double meanOriginal = sumOriginal / count;

            double numerator = 0.0;
            double sumSqReconstructed = 0.0;
            double sumSqOriginal = 0.0;
            foreach (var kv in reconstructed.Conductivities)
            {
                original.Conductivities.TryGetValue(kv.Key, out double origVal);
                double centeredReconstructed = kv.Value - meanReconstructed;
                double centeredOriginal = origVal - meanOriginal;
                numerator += centeredReconstructed * centeredOriginal;
                sumSqReconstructed += centeredReconstructed * centeredReconstructed;
                sumSqOriginal += centeredOriginal * centeredOriginal;
            }

            double denominator = Math.Sqrt(sumSqReconstructed * sumSqOriginal);
            if (denominator < 1e-12)
                return 0.0;

            return numerator / denominator;
        }

        public void StartBackgroundReconstruction()
        {
            InitializeReconstruction(force: true);
            ResetReconstructionMetrics();
            BeginReconstructionMetrics();
            _reconstructionService.StartBackgroundReconstruction(MaxIterationCount, StepSize, RegularizationWeight, ExcitationCurrentAmplitude);
        }

        public void PauseReconstruction()
        {
            _reconstructionService.PauseBackgroundReconstruction();
            PauseReconstructionMetrics();
        }

        public void ResumeReconstruction()
        {
            _reconstructionService.ResumeBackgroundReconstruction();
            BeginReconstructionMetrics();
        }

        public void StopReconstruction()
        {
            _reconstructionService.StopBackgroundReconstruction();
            StopReconstructionMetrics();
            _initializedDiscretization = null;
        }

        public async Task<ReconstructionFrame?> StepReconstructionAsync()
        {
            InitializeReconstruction();
            BeginReconstructionMetrics();
            try
            {
                return await _reconstructionService.StepReconstructionAsync();
            }
            finally
            {
                PauseReconstructionMetrics();
            }
        }

        public Task<ReconstructionResult?> RunFullReconstructionCycleAsync()
        {
            InitializeReconstruction();
            BeginReconstructionMetrics();
            return _reconstructionService.RunFullReconstructionCycleAsync(StepSize,
                                                                         RegularizationWeight,
                                                                         ExcitationCurrentAmplitude);
        }

        private void OnServiceReconstructionUpdated(object? sender, ReconstructionResult result)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IterationCount++;
                Residual = CalculateResidual(result.ReconstructedConductivityDistribution,
                                             result.OriginalConductivityDistribution);
                Correlation = CalculateCorrelation(result.ReconstructedConductivityDistribution,
                                                    result.OriginalConductivityDistribution);

                ResidualHistory.Add(Residual);
                ElapsedTime = _reconstructionStopwatch.Elapsed;

                ReconstructionUpdated?.Invoke(this, result);
            });
        }

        private void OnServiceFrameUpdated(object? sender, ReconstructionFrame frame)
            => ReconstructionFrameUpdated?.Invoke(this, frame);

        public void SaveReconstruction()
        {
            var results = Workspace.GetReconstructionResults();
            if (results.Count == 0 || string.IsNullOrWhiteSpace(Name))
                return;
            _reconstructionService.SaveReconstruction(results, Name, ReconstructionParameters);
            LoadAvailableReconstructions();
        }

        public void LoadReconstruction(string filePath)
        {
            _reconstructionService.LoadReconstruction(filePath);
        }

        public void LoadAvailableReconstructions()
        {
            AvailableReconstructions.Clear();
            foreach (var r in _reconstructionService.GetReconstructions())
                AvailableReconstructions.Add(r);
            ApplyReconstructionFilter();
        }

        private void ApplyReconstructionFilter()
        {
            FilteredReconstructions.Clear();
            foreach (var r in AvailableReconstructions.Where(r =>
                         string.IsNullOrWhiteSpace(ReconstructionSearchText) ||
                         r.Name.Contains(ReconstructionSearchText, StringComparison.OrdinalIgnoreCase)))
                FilteredReconstructions.Add(r);
        }
    }
}