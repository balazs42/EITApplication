using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using ServiceLayer;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;
using Utility.Rendering;

using Workspace = Utility.Classes.Application.Workspace;
using Timer = System.Timers.Timer;
using ElectricalImpedanceTomography.Helpers;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        private readonly IReconstructionService _reconstructionService;
        private readonly IReconstructionExportService _exportService;

        private readonly Stopwatch _reconstructionStopwatch = new();
        private readonly Timer _elapsedTimer;

        private IDiscretization? _discretization = Workspace.GetDiscretization();
        private IDiscretization? _initializedDiscretization;
        private ReconstructionRunSignature? _lastRunSignature;
        private bool _resetMetricsOnStart = true;
        private bool _updatingDrivePatternSelection;
        private EITReconstructionParameters? _trackedParameters;
        private MeasurementSourceOption _selectedMeasurementSource = Workspace.GetMeasurementSource();

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
        private string parallelizationToggleLabel = "Use OMP Parallelization";

        public bool UseParallelizationToggle
        {
            get
            {
                var parameters = ReconstructionParameters;
                if (parameters == null)
                    return false;

                return parameters.DifferentialEquationSolver == DifferentialEquationSolver.LBM
                    ? parameters.UseCudaAcceleration
                    : parameters.UseOmpParallelization;
            }
            set
            {
                if (ReconstructionParameters == null)
                    return;

                if (ReconstructionParameters.DifferentialEquationSolver == DifferentialEquationSolver.LBM)
                    ReconstructionParameters.UseCudaAcceleration = value;
                else
                    ReconstructionParameters.UseOmpParallelization = value;

                OnPropertyChanged(nameof(UseParallelizationToggle));
            }
        }

        public bool HasMeasurementSourceOptions => Workspace.GetImportedMeasurement() != null;

        public bool IsSimulatedMeasurementSelected
        {
            get => SelectedMeasurementSource == MeasurementSourceOption.Simulated;
            set
            {
                if (value)
                    SelectedMeasurementSource = MeasurementSourceOption.Simulated;
            }
        }

        public bool IsRealMeasurementSelected
        {
            get => SelectedMeasurementSource == MeasurementSourceOption.Real;
            set
            {
                if (value)
                    SelectedMeasurementSource = MeasurementSourceOption.Real;
            }
        }

        public string RealMeasurementOptionLabel
        {
            get
            {
                var label = Workspace.GetImportedMeasurementLabel();
                if (HasMeasurementSourceOptions && !string.IsNullOrWhiteSpace(label))
                    return $"Real ({label})";
                return "Real";
            }
        }

        private string _electrodeMeasurementSetupLabel = FormatMeasurementSetupLabel(Workspace.GetElectrodeMeasurementSetup());

        public string ElectrodeMeasurementSetupLabel
        {
            get => _electrodeMeasurementSetupLabel;
            private set => SetProperty(ref _electrodeMeasurementSetupLabel, value);
        }

        private static string FormatMeasurementSetupLabel(ElectrodeMeasurementSetup setup) => setup == ElectrodeMeasurementSetup.Active
            ? "Electrode measurement setup: Active (excitation electrodes are sampled)"
            : "Electrode measurement setup: Non-active (excitation electrodes are ignored)";

        private void OnElectrodeMeasurementSetupChanged(ElectrodeMeasurementSetup setup)
        {
            ElectrodeMeasurementSetupLabel = FormatMeasurementSetupLabel(setup);
        }

        public void RefreshMeasurementSourceOptions() => RefreshMeasurementSourceSelection();

        private MeasurementSourceOption SelectedMeasurementSource
        {
            get => _selectedMeasurementSource;
            set
            {
                var desired = value;
                if (desired == MeasurementSourceOption.Real && Workspace.GetImportedMeasurement() == null)
                    desired = MeasurementSourceOption.Simulated;

                Workspace.SetMeasurementSource(desired);
                var actual = Workspace.GetMeasurementSource();

                if (_selectedMeasurementSource != actual)
                {
                    _selectedMeasurementSource = actual;
                    OnPropertyChanged(nameof(IsSimulatedMeasurementSelected));
                    OnPropertyChanged(nameof(IsRealMeasurementSelected));
                }
                else if (_selectedMeasurementSource != desired)
                {
                    // Requested source was not available; update bindings to reflect the enforced value.
                    OnPropertyChanged(nameof(IsSimulatedMeasurementSelected));
                    OnPropertyChanged(nameof(IsRealMeasurementSelected));
                }
            }
        }

        private void RefreshMeasurementSourceSelection()
        {
            if (!HasMeasurementSourceOptions && _selectedMeasurementSource == MeasurementSourceOption.Real)
            {
                Workspace.SetMeasurementSource(MeasurementSourceOption.Simulated);
            }

            _selectedMeasurementSource = Workspace.GetMeasurementSource();
            OnPropertyChanged(nameof(HasMeasurementSourceOptions));
            OnPropertyChanged(nameof(RealMeasurementOptionLabel));
            OnPropertyChanged(nameof(IsSimulatedMeasurementSelected));
            OnPropertyChanged(nameof(IsRealMeasurementSelected));
            ElectrodeMeasurementSetupLabel = FormatMeasurementSetupLabel(Workspace.GetElectrodeMeasurementSetup());
        }

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string reconstructionSearchText = string.Empty;

        [ObservableProperty]
        private bool videoExportIsRunning;

        [ObservableProperty]
        private bool videoExportHasResult;

        [ObservableProperty]
        private bool videoExportWasSuccessful;

        [ObservableProperty]
        private bool videoExportIsConfiguring;

        [ObservableProperty]
        private string videoExportHeading = "Video Generation in Progress";

        [ObservableProperty]
        private string videoExportStatusMessage = string.Empty;

        [ObservableProperty]
        private double videoExportProgress;

        [ObservableProperty]
        private string videoExportProgressPercentText = "0%";

        [ObservableProperty]
        private string? videoExportFilePath;

        [ObservableProperty]
        private VideoExportResult? videoExportResult;

        [ObservableProperty]
        private VideoExportFormatOption? selectedVideoExportFormat;

        [ObservableProperty]
        private string videoExportEstimatedSizeText = string.Empty;

        [ObservableProperty]
        private string videoExportEstimatedTimeText = string.Empty;

        [ObservableProperty]
        private bool videoExportCanStart;

        public ObservableCollection<VideoExportFormatOption> VideoExportFormatOptions { get; } = [];

        public ObservableCollection<ReconstructionInfo> AvailableReconstructions { get; } = [];
        public ObservableCollection<ReconstructionInfo> FilteredReconstructions { get; } = [];

        private readonly Dictionary<string, ObservableCollection<double>> _metricTrendHistories = new();
        private const int TrendCanvasUpdateInterval = 10;
        private bool _hasPendingTrendUpdate;

        public ObservableCollection<double> ResidualHistory => GetTrendHistory(MetricKeys.Residual);

        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        public ObservableCollection<ReconstructionMetricGroupViewModel> MetricGroups { get; } = [];

        private readonly Dictionary<string, ReconstructionMetricViewModel> _metricsByKey = new();
        private readonly object _metricUpdateLock = new();
        private readonly object _gradientLock = new();
        private readonly object _errorMetricLock = new();
        private CancellationTokenSource? _metricUpdateCts;
        private readonly object _statisticsUpdateLock = new();
        private CancellationTokenSource? _statisticsUpdateCts;
        private ReconstructionResult? _latestResult;
        private ReconstructionFrame? _latestFrame;
        private Dictionary<int, double>? _previousGradientSnapshot;
        private readonly List<GradientHistorySample> _gradientHistory = new();
        private List<int>? _gradientElementOrder;
        private Dictionary<int, int>? _gradientElementIndexMap;
        private int _selectedGradientIndex = -1;
        public event EventHandler? GradientHistoryChanged;
        public event EventHandler<int>? GradientSelectionChanged;
        public event EventHandler? GradientInspectionRequested;
        private IErrorMetric? _cachedErrorMetric;
        private ErrorMetric _cachedErrorMetricChoice;

        public event EventHandler? SelectedTrendMetricHistoryChanged;

        [ObservableProperty]
        private string selectedTrendMetricKey = MetricKeys.Residual;

        private SKSizeI _videoExportDistributionSize;
        private SKSizeI _videoExportColorbarSize;
        private SKSizeI _videoExportResidualSize;
        private SKSizeI _videoExportFrameSize;
        private int _videoExportFrameCount;

        partial void OnSelectedVideoExportFormatChanged(VideoExportFormatOption? value)
        {
            UpdateVideoExportEstimates();
        }

        partial void OnVideoExportIsRunningChanged(bool value)
        {
            UpdateVideoExportPhase();
        }

        partial void OnVideoExportHasResultChanged(bool value)
        {
            UpdateVideoExportPhase();
        }

        public ReconstructionPageViewModel(IReconstructionService reconstructionService,
                                           IReconstructionExportService exportService)
        {
            _reconstructionService = reconstructionService;
            _exportService = exportService;

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

            InitializeMetricGroups();
            PropertyChanged += OnViewModelPropertyChanged;
            TrackReconstructionParameters(ReconstructionParameters);
            SyncDrivePatternSelection();
            UpdateParallelizationToggleState();
            RefreshMeasurementSourceSelection();
            ElectrodeMeasurementSetupLabel = FormatMeasurementSetupLabel(Workspace.GetElectrodeMeasurementSetup());
            Workspace.ElectrodeMeasurementSetupChanged += OnElectrodeMeasurementSetupChanged;

            //UpdateMetric(MetricKeys.ErrorMetric, ReconstructionParameters.ErrorMetric.ToString());
            //UpdateMetric(MetricKeys.RegularizationWeight, FormatDouble(RegularizationWeight, "G3"));
            UpdateMetric(MetricKeys.IterationCount, IterationCount.ToString(CultureInfo.InvariantCulture));
            UpdateMetric(MetricKeys.ElapsedTime, FormatElapsed(ElapsedTime));
            UpdateMetric(MetricKeys.Residual, FormatDouble(Residual));
            UpdateMetric(MetricKeys.Correlation, FormatDouble(Correlation));

            _reconstructionService.ReconstructionUpdated += OnServiceReconstructionUpdated;
            _reconstructionService.ReconstructionFrameUpdated += OnServiceFrameUpdated;
        }

        public void PrepareForNewReconstruction()
        {
            UpdateMesh();
            UpdateReconstructionParameters();

            var mesh = _discretization ?? throw new NullReferenceException("Mesh was null during reconstruction initialization, check calling code!");

            var signature = CreateCurrentRunSignature(mesh);
            bool isSameRun = _lastRunSignature?.Equals(signature) ?? false;

            if (_initializedDiscretization != mesh)
            {
                isSameRun = false;
                _reconstructionService.InitializeReconstruction(mesh, ReconstructionParameters, true);
                _initializedDiscretization = mesh;
                IterationCount = 0;
            }
            else if (!isSameRun)
            {
                _reconstructionService.InitializeReconstruction(mesh, ReconstructionParameters, true);
                IterationCount = 0;
            }

            if (!isSameRun)
                _resetMetricsOnStart = true;

            _lastRunSignature = signature;
        }

        private void TrackReconstructionParameters(EITReconstructionParameters parameters)
        {
            if (_trackedParameters != null)
                _trackedParameters.PropertyChanged -= OnTrackedParametersPropertyChanged;

            _trackedParameters = parameters;
            if (_trackedParameters != null)
                _trackedParameters.PropertyChanged += OnTrackedParametersPropertyChanged;
        }

        private void OnTrackedParametersPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EITReconstructionParameters.DrivePattern))
                SyncDrivePatternSelection();
            else if (e.PropertyName == nameof(EITReconstructionParameters.DifferentialEquationSolver)
                     || e.PropertyName == nameof(EITReconstructionParameters.UseOmpParallelization)
                     || e.PropertyName == nameof(EITReconstructionParameters.UseCudaAcceleration))
                UpdateParallelizationToggleState();
        }

        private void SyncDrivePatternSelection()
        {
            if (ReconstructionParameters == null)
                return;

            SetDrivePattern(ReconstructionParameters.DrivePattern);
        }

        private void UpdateParallelizationToggleState()
        {
            if (ReconstructionParameters == null)
                return;

            ParallelizationToggleLabel = ReconstructionParameters.DifferentialEquationSolver == DifferentialEquationSolver.LBM
                ? "Use CUDA Parallelization"
                : "Use OMP Parallelization";

            OnPropertyChanged(nameof(UseParallelizationToggle));
        }

        public void SetDrivePattern(DrivePattern pattern)
        {
            if (ReconstructionParameters == null)
                return;

            if (_updatingDrivePatternSelection)
                return;

            try
            {
                _updatingDrivePatternSelection = true;
                AdjecentDrivePattern = pattern == DrivePattern.Adjecent;
                OppositeDrivePattern = pattern == DrivePattern.Opposite;
                ReconstructionParameters.DrivePattern = pattern;
            }
            finally
            {
                _updatingDrivePatternSelection = false;
            }
        }

        private void InitializeMetricGroups()
        {
            MetricGroups.Clear();
            _metricsByKey.Clear();

            RegisterMetric("Progress & Timing", MetricKeys.ElapsedTime, "Elapsed Time");
            RegisterMetric("Progress & Timing", MetricKeys.IterationCount, "Iterations");
            RegisterMetric("Progress & Timing", MetricKeys.IterationsPerSecond, "Iterations / Second");
            RegisterMetric("Progress & Timing", MetricKeys.TimePerIteration, "Seconds / Iteration");

            RegisterMetric("Error Norms", MetricKeys.Residual, "Residual L2 Norm", TrendMetricCategory.Residual);
            RegisterMetric("Error Norms", MetricKeys.Rmse, "RMSE (σ)", TrendMetricCategory.ErrorNorm);
            RegisterMetric("Error Norms", MetricKeys.Mae, "MAE (σ)", TrendMetricCategory.ErrorNorm);
            RegisterMetric("Error Norms", MetricKeys.Mape, "MAPE (σ)", TrendMetricCategory.ErrorNorm);
            RegisterMetric("Error Norms", MetricKeys.ResidualDropPerIteration, "Residual Drop / Iteration");

            RegisterMetric("Similarity Scores", MetricKeys.Correlation, "Pearson Correlation", TrendMetricCategory.Similarity);
            RegisterMetric("Similarity Scores", MetricKeys.Psnr, "PSNR (dB)", TrendMetricCategory.Similarity);
            RegisterMetric("Similarity Scores", MetricKeys.Ssim, "SSIM", TrendMetricCategory.Similarity);

            RegisterMetric("Improvement", MetricKeys.RmseImprovement, "RMSE Improvement vs. Initial");
            RegisterMetric("Improvement", MetricKeys.MaeImprovement, "MAE Improvement vs. Initial");

            //RegisterMetric("Misfit & Regularization", MetricKeys.ErrorMetric, "Misfit Metric");
            //RegisterMetric("Misfit & Regularization", MetricKeys.MisfitValue, "Misfit Value");
            //RegisterMetric("Misfit & Regularization", MetricKeys.RegularizationWeight, "Regularization Weight");
            //RegisterMetric("Misfit & Regularization", MetricKeys.RegularizationEnergy, "Regularization Energy");
            //RegisterMetric("Misfit & Regularization", MetricKeys.RegularizationRange, "Regularization Range");

            RegisterMetric("Gradient & Field Diagnostics", MetricKeys.GradientNorm, "Gradient L2 Norm");
            RegisterMetric("Gradient & Field Diagnostics", MetricKeys.GradientAngleChange, "Gradient Angle Δ");
            RegisterMetric("Gradient & Field Diagnostics", MetricKeys.PotentialRange, "Potential Range");
            RegisterMetric("Gradient & Field Diagnostics", MetricKeys.AdjointRange, "Adjoint Range");

            //RegisterMetric("Electrode Measurements", MetricKeys.ElectrodeRmse, "Electrode RMSE");
            //RegisterMetric("Electrode Measurements", MetricKeys.ElectrodeMae, "Electrode MAE");
            //RegisterMetric("Electrode Measurements", MetricKeys.ElectrodeMape, "Electrode MAPE");

            UpdateTrendSelectionStates();
        }

        private void RegisterMetric(string groupTitle, string key, string name, TrendMetricCategory trendCategory = TrendMetricCategory.None)
        {
            var group = MetricGroups.FirstOrDefault(g => g.Title == groupTitle);
            if (group is null)
            {
                group = new ReconstructionMetricGroupViewModel(groupTitle);
                MetricGroups.Add(group);
            }

            var metricVm = new ReconstructionMetricViewModel(key, name, trendCategory);
            group.Metrics.Add(metricVm);
            _metricsByKey[key] = metricVm;

            if (metricVm.IsTrendSelectable)
                _ = GetTrendHistory(key);
        }

        private void UpdateMetric(string key, string value)
        {
            if (_metricsByKey.TryGetValue(key, out var metric))
                metric.Value = value;
        }

        public ReconstructionMetricViewModel? GetMetricByKey(string key)
            => _metricsByKey.TryGetValue(key, out var metric) ? metric : null;

        public IReadOnlyList<double> GetTrendHistorySnapshot(string key)
        {
            var history = GetTrendHistory(key);
            return history.ToArray();
        }

        public IReadOnlyList<double> GetSelectedTrendHistorySnapshot()
            => GetTrendHistorySnapshot(SelectedTrendMetricKey);

        public IReadOnlyList<GradientHistorySample> GetGradientHistorySnapshot()
        {
            lock (_gradientLock)
            {
                return _gradientHistory
                    .Select(sample => new GradientHistorySample(sample.Iteration,
                                                                  sample.GetVectorCopy(),
                                                                  sample.Norm,
                                                                  sample.FrameIndex,
                                                                  sample.Angle))
                    .ToList();
            }
        }

        public GradientHistorySample? GetGradientSample(int index)
        {
            lock (_gradientLock)
            {
                if (index < 0 || index >= _gradientHistory.Count)
                    return null;

                var sample = _gradientHistory[index];
                return new GradientHistorySample(sample.Iteration,
                                                 sample.GetVectorCopy(),
                                                 sample.Norm,
                                                 sample.FrameIndex,
                                                 sample.Angle);
            }
        }

        public int SelectedGradientIndex
        {
            get
            {
                lock (_gradientLock)
                {
                    return _selectedGradientIndex;
                }
            }
        }

        public int GradientHistoryCount
        {
            get
            {
                lock (_gradientLock)
                {
                    return _gradientHistory.Count;
                }
            }
        }

        public void SetSelectedGradientIndex(int index)
        {
            if (index < -1)
                return;

            bool changed = false;
            lock (_gradientLock)
            {
                if (index >= _gradientHistory.Count)
                    return;

                if (_selectedGradientIndex == index)
                    return;

                _selectedGradientIndex = index;
                changed = true;
            }

            if (changed)
                GradientSelectionChanged?.Invoke(this, index);
        }

        public void SnapGradientSelectionToFrame(int frameIndex)
        {
            if (frameIndex < 0)
                frameIndex = 0;

            int targetIndex;
            lock (_gradientLock)
            {
                if (_gradientHistory.Count == 0)
                    return;

                targetIndex = -1;
                for (int i = 0; i < _gradientHistory.Count; i++)
                {
                    if (_gradientHistory[i].FrameIndex <= frameIndex)
                        targetIndex = i;
                    else
                        break;
                }

                if (targetIndex < 0)
                    targetIndex = 0;

                if (targetIndex == _selectedGradientIndex)
                    return;

                _selectedGradientIndex = targetIndex;
            }

            GradientSelectionChanged?.Invoke(this, targetIndex);
        }

        public void ResetReconstructionMetrics()
        {
            ResetMetricsCore();
            _resetMetricsOnStart = false;
        }

        public void BeginReconstructionMetrics()
        {
            if (_resetMetricsOnStart)
            {
                ResetMetricsCore();
                _resetMetricsOnStart = false;
            }

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

            FlushPendingTrendUpdates();
            StopElapsedTimer();
        }

        public void StopReconstructionMetrics()
        {
            ResetMetricsCore();
            _resetMetricsOnStart = true;
        }

        private void ResetMetricsCore()
        {
            foreach (var history in _metricTrendHistories.Values)
                history.Clear();
            Residual = 0.0;
            Correlation = 0.0;
            ElapsedTime = TimeSpan.Zero;
            IterationCount = 0;
            _hasPendingTrendUpdate = false;
            _reconstructionStopwatch.Reset();

            ClearGradientHistory();

            RaiseSelectedTrendMetricHistoryChanged();

            lock (_metricUpdateLock)
            {
                _metricUpdateCts?.Cancel();
                _metricUpdateCts = null;
                _latestResult = null;
                _latestFrame = null;
            }

            lock (_gradientLock)
            {
                _previousGradientSnapshot = null;
            }

            ResetDynamicMetrics();
        }

        private void ClearGradientHistory()
        {
            bool hadHistory;
            int previousIndex;
            lock (_gradientLock)
            {
                hadHistory = _gradientHistory.Count > 0;
                previousIndex = _selectedGradientIndex;
                _gradientHistory.Clear();
                _gradientElementOrder = null;
                _gradientElementIndexMap = null;
                _previousGradientSnapshot = null;
                _selectedGradientIndex = -1;
            }

            if (hadHistory || previousIndex != -1)
            {
                GradientHistoryChanged?.Invoke(this, EventArgs.Empty);
                GradientSelectionChanged?.Invoke(this, -1);
            }
        }

        private void ResetDynamicMetrics()
        {
            foreach (var metric in _metricsByKey.Values)
                metric.Value = "—";

            //UpdateMetric(MetricKeys.ErrorMetric, ReconstructionParameters.ErrorMetric.ToString());
            //UpdateMetric(MetricKeys.RegularizationWeight, FormatDouble(RegularizationWeight, "G3"));
            UpdateMetric(MetricKeys.IterationCount, IterationCount.ToString(CultureInfo.InvariantCulture));
            UpdateMetric(MetricKeys.ElapsedTime, FormatElapsed(TimeSpan.Zero));
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            //if (e.PropertyName == nameof(RegularizationWeight))
            //    UpdateMetric(MetricKeys.RegularizationWeight, FormatDouble(RegularizationWeight, "G3"));
            //else if (e.PropertyName == nameof(ReconstructionParameters))
            //    UpdateMetric(MetricKeys.ErrorMetric, ReconstructionParameters.ErrorMetric.ToString());
            if (e.PropertyName == nameof(ReconstructionParameters))
            {
                TrackReconstructionParameters(ReconstructionParameters);
                SyncDrivePatternSelection();
                UpdateParallelizationToggleState();
            }
        }

        partial void OnElapsedTimeChanged(TimeSpan value)
        {
            UpdateMetric(MetricKeys.ElapsedTime, FormatElapsed(value));
            UpdateIterationsPerSecond();
            UpdateTimePerIteration();
        }

        partial void OnIterationCountChanged(int value)
        {
            UpdateMetric(MetricKeys.IterationCount, value.ToString(CultureInfo.InvariantCulture));
            UpdateIterationsPerSecond();
            UpdateTimePerIteration();
        }

        partial void OnResidualChanged(double value)
            => UpdateMetric(MetricKeys.Residual, FormatDouble(value));

        partial void OnCorrelationChanged(double value)
            => UpdateMetric(MetricKeys.Correlation, FormatDouble(value));

        private void UpdateIterationsPerSecond()
        {
            if (IterationCount <= 0 || ElapsedTime.TotalSeconds <= 1e-6)
            {
                UpdateMetric(MetricKeys.IterationsPerSecond, "—");
                return;
            }

            double ips = IterationCount / Math.Max(ElapsedTime.TotalSeconds, 1e-6);
            UpdateMetric(MetricKeys.IterationsPerSecond, FormatDouble(ips, "F2"));
        }

        private void UpdateTimePerIteration()
        {
            if (IterationCount <= 0)
            {
                UpdateMetric(MetricKeys.TimePerIteration, "—");
                return;
            }

            double seconds = ElapsedTime.TotalSeconds / Math.Max(IterationCount, 1);
            UpdateMetric(MetricKeys.TimePerIteration, $"{seconds.ToString("F2", CultureInfo.InvariantCulture)} s");
        }

        private void UpdateResidualTrendMetrics()
        {
            if (ResidualHistory.Count <= 1)
            {
                UpdateMetric(MetricKeys.ResidualDropPerIteration, "—");
                return;
            }

            double first = ResidualHistory[0];
            double last = ResidualHistory[^1];
            int steps = Math.Max(ResidualHistory.Count - 1, 1);
            double drop = (first - last) / steps;
            UpdateMetric(MetricKeys.ResidualDropPerIteration, FormatDouble(drop));
        }

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
        private void UpdateMesh()
        {
            _discretization = Workspace.GetDiscretization();
            RefreshMeasurementSourceSelection();
        }
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
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FEM)
                    return false;
            else if(_discretization is LBMGrid)
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.LBM)
                    return false;

            return true;
        }

        private static double CalculateResidual(ReconstructionResult result)
        {
            if (result.Frames.Count == 0)
                return 0.0;

            double sumSq = 0.0;
            int sampleCount = 0;

            foreach (var frame in result.Frames)
            {
                var measured = frame.MeasuredElectrodeValues;
                var simulated = frame.SimulatedElectrodeValues;

                if (measured == null || simulated == null)
                    continue;

                int length = Math.Min(measured.Length, simulated.Length);
                for (int i = 0; i < length; i++)
                {
                    double measuredValue = measured[i];
                    double simulatedValue = simulated[i];

                    if (double.IsNaN(measuredValue) || double.IsInfinity(measuredValue))
                        continue;
                    if (double.IsNaN(simulatedValue) || double.IsInfinity(simulatedValue))
                        continue;

                    double diff = simulatedValue - measuredValue;
                    sumSq += diff * diff;
                    sampleCount++;
                }
            }

            if (sampleCount == 0)
                return 0.0;

            // * 1000 for mV
            return Math.Sqrt(sumSq / sampleCount) * 1000;
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

        private void RequestMetricUpdate(ReconstructionResult? result, ReconstructionFrame? frame)
        {
            lock (_metricUpdateLock)
            {
                if (result != null)
                    _latestResult = result;
                if (frame != null)
                    _latestFrame = frame;

                var snapshotResult = _latestResult;
                var snapshotFrame = _latestFrame;

                if (snapshotResult is null && snapshotFrame is null)
                    return;

                _metricUpdateCts?.Cancel();
                _metricUpdateCts = new CancellationTokenSource();
                var token = _metricUpdateCts.Token;

                Task.Run(() => ComputeMetricsAsync(snapshotResult, snapshotFrame, token), token);
            }
        }

        private async Task ComputeMetricsAsync(ReconstructionResult? result, ReconstructionFrame? frame, CancellationToken token)
        {
            try
            {
                DistributionMetrics? distributionMetrics = null;
                if (result != null)
                {
                    distributionMetrics = ComputeDistributionMetrics(result, token);
                }

                token.ThrowIfCancellationRequested();

                var measurementMetrics = ComputeElectrodeMetrics(token);

                token.ThrowIfCancellationRequested();

                frame ??= result?.Frames.LastOrDefault();
                var fieldMetrics = ComputeFieldMetrics(frame, token);

                token.ThrowIfCancellationRequested();

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (distributionMetrics.HasValue)
                    {
                        var metrics = distributionMetrics.Value;
                        UpdateMetric(MetricKeys.Rmse, FormatDouble(metrics.Rmse));
                        UpdateMetric(MetricKeys.Mae, FormatDouble(metrics.Mae));
                        UpdateMetric(MetricKeys.Mape, FormatPercent(metrics.Mape));
                        UpdateMetric(MetricKeys.Psnr, FormatDouble(metrics.Psnr, "F2"));
                        UpdateMetric(MetricKeys.Ssim, FormatDouble(metrics.Ssim, "F3"));
                        UpdateMetric(MetricKeys.RmseImprovement, FormatPercent(metrics.RmseImprovement));
                        UpdateMetric(MetricKeys.MaeImprovement, FormatPercent(metrics.MaeImprovement));

                        AddTrendSample(MetricKeys.Rmse, metrics.Rmse);
                        AddTrendSample(MetricKeys.Mae, metrics.Mae);
                        AddTrendSample(MetricKeys.Mape, metrics.Mape);
                        AddTrendSample(MetricKeys.Psnr, metrics.Psnr);
                        AddTrendSample(MetricKeys.Ssim, metrics.Ssim);
                    }

                    //if (measurementMetrics.HasValue)
                    //{
                    //    var m = measurementMetrics.Value;
                    //    UpdateMetric(MetricKeys.ElectrodeRmse, FormatDouble(m.Rmse));
                    //    UpdateMetric(MetricKeys.ElectrodeMae, FormatDouble(m.Mae));
                    //    UpdateMetric(MetricKeys.ElectrodeMape, FormatPercent(m.Mape));
                    //    UpdateMetric(MetricKeys.MisfitValue, m.Misfit.HasValue ? FormatDouble(m.Misfit.Value, "G4") : "—");
                    //}
                    //else
                    //{
                    //    UpdateMetric(MetricKeys.ElectrodeRmse, "—");
                    //    UpdateMetric(MetricKeys.ElectrodeMae, "—");
                    //    UpdateMetric(MetricKeys.ElectrodeMape, "—");
                    //    UpdateMetric(MetricKeys.MisfitValue, "—");
                    //}

                    if (fieldMetrics.HasData)
                    {
                        UpdateMetric(MetricKeys.GradientNorm, FormatDouble(fieldMetrics.GradientNorm));
                        UpdateMetric(MetricKeys.GradientAngleChange,
                                     fieldMetrics.GradientAngle.HasValue
                                         ? $"{FormatDouble(fieldMetrics.GradientAngle.Value, "F1")}°"
                                         : "—");
                        UpdateMetric(MetricKeys.PotentialRange, FormatRange(fieldMetrics.PotentialRange));
                        UpdateMetric(MetricKeys.AdjointRange, FormatRange(fieldMetrics.AdjointRange));
                        //UpdateMetric(MetricKeys.RegularizationRange, FormatRange(fieldMetrics.RegularizationRange));
                        //UpdateMetric(MetricKeys.RegularizationEnergy, FormatDouble(fieldMetrics.RegularizationEnergy));

                        if (fieldMetrics.GradientSnapshot != null)
                        {
                            lock (_gradientLock)
                            {
                                _previousGradientSnapshot = fieldMetrics.GradientSnapshot;
                            }

                            RecordGradientSnapshot(fieldMetrics);
                        }
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Metric computation failed: {ex}");
            }
        }

        private static DistributionMetrics? ComputeDistributionMetrics(ReconstructionResult result, CancellationToken token)
        {
            var reconstructed = result.ReconstructedConductivityDistribution.Conductivities;
            if (reconstructed.Count == 0)
                return null;

            var original = result.OriginalConductivityDistribution.Conductivities;
            var initial = result.InitialConductivitiyDistribution.Conductivities;

            int count = reconstructed.Count;
            double[] recon = new double[count];
            double[] orig = new double[count];
            double[] init = new double[count];

            double sumSq = 0.0;
            double sumAbs = 0.0;
            double sumPct = 0.0;
            double maxAbs = 0.0;

            int index = 0;
            foreach (var kv in reconstructed)
            {
                token.ThrowIfCancellationRequested();

                double r = kv.Value;
                original.TryGetValue(kv.Key, out double o);
                initial.TryGetValue(kv.Key, out double i);

                recon[index] = r;
                orig[index] = o;
                init[index] = i;

                double diff = r - o;
                sumSq += diff * diff;
                sumAbs += Math.Abs(diff);
                sumPct += Math.Abs(diff) / Math.Max(Math.Abs(o), 1e-6);

                maxAbs = Math.Max(maxAbs, Math.Abs(o));
                maxAbs = Math.Max(maxAbs, Math.Abs(r));
                index++;
            }

            double mse = sumSq / Math.Max(count, 1);
            double rmse = Math.Sqrt(mse);
            double mae = sumAbs / Math.Max(count, 1);
            double mape = sumPct / Math.Max(count, 1);

            double psnr;
            if (mse <= 1e-12)
            {
                psnr = double.PositiveInfinity;
            }
            else
            {
                double peak = maxAbs <= 1e-12 ? 1.0 : maxAbs;
                psnr = 20.0 * Math.Log10(peak / Math.Sqrt(mse));
            }

            double initialRmse = 0.0;
            double initialMae = 0.0;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                double diffInit = init[i] - orig[i];
                initialRmse += diffInit * diffInit;
                initialMae += Math.Abs(diffInit);
            }

            initialRmse = Math.Sqrt(initialRmse / Math.Max(count, 1));
            initialMae /= Math.Max(count, 1);

            double rmseImprovement = initialRmse > 1e-9 ? (initialRmse - rmse) / initialRmse : 0.0;
            double maeImprovement = initialMae > 1e-9 ? (initialMae - mae) / initialMae : 0.0;

            double ssim = ComputeSsim(orig, recon);

            return new DistributionMetrics(rmse, mae, mape, psnr, ssim, rmseImprovement, maeImprovement);
        }

        private static double ComputeSsim(double[] reference, double[] test)
        {
            if (reference.Length == 0 || reference.Length != test.Length)
                return double.NaN;

            double meanRef = 0.0;
            double meanTest = 0.0;
            for (int i = 0; i < reference.Length; i++)
            {
                meanRef += reference[i];
                meanTest += test[i];
            }

            int n = reference.Length;
            meanRef /= n;
            meanTest /= n;

            double varianceRef = 0.0;
            double varianceTest = 0.0;
            double covariance = 0.0;

            for (int i = 0; i < n; i++)
            {
                double refDelta = reference[i] - meanRef;
                double testDelta = test[i] - meanTest;
                varianceRef += refDelta * refDelta;
                varianceTest += testDelta * testDelta;
                covariance += refDelta * testDelta;
            }

            varianceRef /= n;
            varianceTest /= n;
            covariance /= n;

            const double c1 = 0.01 * 0.01;
            const double c2 = 0.03 * 0.03;

            double numerator = (2 * meanRef * meanTest + c1) * (2 * covariance + c2);
            double denominator = (meanRef * meanRef + meanTest * meanTest + c1) * (varianceRef + varianceTest + c2);

            if (denominator <= 1e-12)
                return double.NaN;

            return numerator / denominator;
        }

        private MeasurementMetrics? ComputeElectrodeMetrics(CancellationToken token)
        {
            var discretization = Workspace.GetDiscretization();
            var original = Workspace.GetOriginalDiscretization();

            if (discretization == null || original == null)
                return null;

            var simulated = discretization.GetElectrodePotentials();
            var measured = original.GetElectrodePotentials();

            if (simulated.Length == 0 || measured.Length == 0 || simulated.Length != measured.Length)
                return null;

            double sumSq = 0.0;
            double sumAbs = 0.0;
            double sumPct = 0.0;

            for (int i = 0; i < simulated.Length; i++)
            {
                token.ThrowIfCancellationRequested();

                double diff = simulated[i] - measured[i];
                sumSq += diff * diff;
                sumAbs += Math.Abs(diff);
                sumPct += Math.Abs(diff) / Math.Max(Math.Abs(measured[i]), 1e-6);
            }

            int n = simulated.Length;
            double rmse = Math.Sqrt(sumSq / Math.Max(n, 1));
            double mae = sumAbs / Math.Max(n, 1);
            double mape = sumPct / Math.Max(n, 1);

            double? misfit = null;
            try
            {
                var metric = GetErrorMetric(ReconstructionParameters.ErrorMetric);
                misfit = metric.Evaluate(discretization, measured, simulated);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Misfit evaluation failed: {ex.Message}");
            }

            return new MeasurementMetrics(rmse, mae, mape, misfit);
        }

        private FieldMetrics ComputeFieldMetrics(ReconstructionFrame? frame, CancellationToken token)
        {
            if (frame == null)
                return FieldMetrics.Empty;

            var gradient = frame.ConductivityGradient.Conductivities;
            double gradientNorm = 0.0;
            foreach (var kv in gradient)
            {
                token.ThrowIfCancellationRequested();
                gradientNorm += kv.Value * kv.Value;
            }
            gradientNorm = Math.Sqrt(gradientNorm);

            double? gradientAngle = null;
            Dictionary<int, double>? previous;
            lock (_gradientLock)
            {
                previous = _previousGradientSnapshot;
            }

            if (previous != null && previous.Count > 0 && gradient.Count > 0)
            {
                gradientAngle = ComputeGradientAngle(previous, gradient);
            }

            var potentialRange = ComputeRange(frame.CalculatedPotentialDistribution?.Potentials);
            var adjointRange = ComputeRange(frame.CalculatedAdjointDistribution?.Potentials);
            var regularizationRange = ComputeRange(frame.CalculatedRegularization?.Conductivities);
            double regularizationEnergy = ComputeRegularizationEnergy(frame.CalculatedRegularization?.Conductivities, token);

            var gradientSnapshot = new Dictionary<int, double>(gradient);

            return new FieldMetrics(true,
                                    gradientNorm,
                                    gradientAngle,
                                    potentialRange,
                                    adjointRange,
                                    regularizationRange,
                                    regularizationEnergy,
                                    gradientSnapshot);
        }

        private void RecordGradientSnapshot(FieldMetrics fieldMetrics)
        {
            if (fieldMetrics.GradientSnapshot == null || fieldMetrics.GradientSnapshot.Count == 0)
                return;

            int selectedIndex;

            lock (_gradientLock)
            {
                var snapshot = fieldMetrics.GradientSnapshot;

                if (_gradientElementOrder is null || _gradientElementIndexMap is null)
                {
                    _gradientElementOrder = snapshot.Keys.ToList();
                    _gradientElementIndexMap = new Dictionary<int, int>(_gradientElementOrder.Count);
                    for (int i = 0; i < _gradientElementOrder.Count; i++)
                        _gradientElementIndexMap[_gradientElementOrder[i]] = i;
                }
                else
                {
                    foreach (var key in snapshot.Keys)
                    {
                        if (!_gradientElementIndexMap.ContainsKey(key))
                        {
                            _gradientElementIndexMap[key] = _gradientElementOrder.Count;
                            _gradientElementOrder.Add(key);
                        }
                    }
                }

                if (_gradientElementOrder is null)
                    return;

                double[] vector = new double[_gradientElementOrder.Count];
                for (int i = 0; i < _gradientElementOrder.Count; i++)
                {
                    int key = _gradientElementOrder[i];
                    snapshot.TryGetValue(key, out double value);
                    vector[i] = value;
                }

                int frameIndex = Math.Max(Workspace.GetReconstructionFrames().Count - 1, 0);
                var sample = new GradientHistorySample(IterationCount,
                                                       vector,
                                                       fieldMetrics.GradientNorm,
                                                       frameIndex,
                                                       fieldMetrics.GradientAngle);
                _gradientHistory.Add(sample);
                _selectedGradientIndex = _gradientHistory.Count - 1;
                selectedIndex = _selectedGradientIndex;
            }

            GradientHistoryChanged?.Invoke(this, EventArgs.Empty);
            GradientSelectionChanged?.Invoke(this, selectedIndex);
        }

        private static double ComputeGradientAngle(Dictionary<int, double> previous, Dictionary<int, double> current)
        {
            double dot = 0.0;
            double prevNorm = 0.0;
            double currNorm = 0.0;

            foreach (var kv in current)
            {
                double value = kv.Value;
                currNorm += value * value;
                if (previous.TryGetValue(kv.Key, out double prevValue))
                    dot += prevValue * value;
            }

            foreach (var kv in previous)
            {
                double value = kv.Value;
                prevNorm += value * value;
            }

            double denom = Math.Sqrt(prevNorm) * Math.Sqrt(currNorm);
            if (denom <= 1e-12)
                return double.NaN;

            double cosTheta = dot / denom;
            cosTheta = Math.Clamp(cosTheta, -1.0, 1.0);
            return Math.Acos(cosTheta) * (180.0 / Math.PI);
        }

        private static RangeMetrics ComputeRange(IReadOnlyDictionary<int, double>? values)
        {
            if (values == null || values.Count == 0)
                return RangeMetrics.Empty;

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            foreach (var value in values.Values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;

                if (value < min)
                    min = value;
                if (value > max)
                    max = value;
            }

            if (double.IsPositiveInfinity(min) || double.IsNegativeInfinity(max))
                return RangeMetrics.Empty;

            return new RangeMetrics(min, max);
        }

        private static double ComputeRegularizationEnergy(IReadOnlyDictionary<int, double>? values, CancellationToken token)
        {
            if (values == null || values.Count == 0)
                return double.NaN;

            double sum = 0.0;
            foreach (var kv in values)
            {
                token.ThrowIfCancellationRequested();
                sum += kv.Value * kv.Value;
            }

            return Math.Sqrt(sum);
        }

        private static string FormatDouble(double value, string format = "F3")
        {
            if (double.IsNaN(value))
                return "—";
            if (double.IsPositiveInfinity(value))
                return "∞";
            if (double.IsNegativeInfinity(value))
                return "-∞";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string FormatPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "—";
            return value.ToString("P1", CultureInfo.InvariantCulture);
        }

        private static string FormatElapsed(TimeSpan value)
            => value.TotalHours >= 1.0
                ? value.ToString("hh\\:mm\\:ss")
                : value.ToString("mm\\:ss");

        private static string FormatRange(RangeMetrics range)
        {
            if (!range.HasValue)
                return "—";

            double delta = range.Max.Value - range.Min.Value;
            return $"{range.Min.Value.ToString("F3", CultureInfo.InvariantCulture)} to {range.Max.Value.ToString("F3", CultureInfo.InvariantCulture)} (Δ {delta.ToString("F3", CultureInfo.InvariantCulture)})";
        }

        public string FormatTrendValue(string metricKey, double value)
            => metricKey switch
            {
                MetricKeys.Mape => FormatPercent(value),
                MetricKeys.RmseImprovement => FormatPercent(value),
                MetricKeys.MaeImprovement => FormatPercent(value),
                MetricKeys.Psnr => FormatDouble(value, "F2"),
                MetricKeys.Ssim => FormatDouble(value, "F3"),
                _ => FormatDouble(value)
            };

        private IErrorMetric GetErrorMetric(ErrorMetric choice)
        {
            lock (_errorMetricLock)
            {
                if (_cachedErrorMetric == null || _cachedErrorMetricChoice != choice)
                {
                    _cachedErrorMetric = ErrorMetricFactory.Create(choice);
                    _cachedErrorMetricChoice = choice;
                }

                return _cachedErrorMetric;
            }
        }

        private static class MetricKeys
        {
            public const string ElapsedTime = "elapsedTime";
            public const string IterationCount = "iterationCount";
            public const string IterationsPerSecond = "iterationsPerSecond";
            public const string TimePerIteration = "timePerIteration";
            public const string Residual = "residual";
            public const string Rmse = "rmse";
            public const string Mae = "mae";
            public const string Mape = "mape";
            public const string ResidualDropPerIteration = "residualDrop";
            public const string Correlation = "correlation";
            public const string Psnr = "psnr";
            public const string Ssim = "ssim";
            public const string RmseImprovement = "rmseImprovement";
            public const string MaeImprovement = "maeImprovement";        
            public const string GradientNorm = "gradientNorm";
            public const string GradientAngleChange = "gradientAngle";
            public const string PotentialRange = "potentialRange";
            public const string AdjointRange = "adjointRange";
        }

        private readonly struct DistributionMetrics
        {
            public DistributionMetrics(double rmse,
                                       double mae,
                                       double mape,
                                       double psnr,
                                       double ssim,
                                       double rmseImprovement,
                                       double maeImprovement)
            {
                Rmse = rmse;
                Mae = mae;
                Mape = mape;
                Psnr = psnr;
                Ssim = ssim;
                RmseImprovement = rmseImprovement;
                MaeImprovement = maeImprovement;
            }

            public double Rmse { get; }
            public double Mae { get; }
            public double Mape { get; }
            public double Psnr { get; }
            public double Ssim { get; }
            public double RmseImprovement { get; }
            public double MaeImprovement { get; }
        }


        private readonly struct MeasurementMetrics
        {
            public MeasurementMetrics(double rmse, double mae, double mape, double? misfit)
            {
                Rmse = rmse;
                Mae = mae;
                Mape = mape;
                Misfit = misfit;
            }

            public double Rmse { get; }
            public double Mae { get; }
            public double Mape { get; }
            public double? Misfit { get; }
        }

        private readonly struct FieldMetrics
        {
            public static FieldMetrics Empty { get; } = new(false,
                                                              0.0,
                                                              null,
                                                              RangeMetrics.Empty,
                                                              RangeMetrics.Empty,
                                                              RangeMetrics.Empty,
                                                              double.NaN,
                                                              null);

            public FieldMetrics(bool hasData,
                                double gradientNorm,
                                double? gradientAngle,
                                RangeMetrics potentialRange,
                                RangeMetrics adjointRange,
                                RangeMetrics regularizationRange,
                                double regularizationEnergy,
                                Dictionary<int, double>? gradientSnapshot)
            {
                HasData = hasData;
                GradientNorm = gradientNorm;
                GradientAngle = gradientAngle;
                PotentialRange = potentialRange;
                AdjointRange = adjointRange;
                RegularizationRange = regularizationRange;
                RegularizationEnergy = regularizationEnergy;
                GradientSnapshot = gradientSnapshot;
            }

            public bool HasData { get; }
            public double GradientNorm { get; }
            public double? GradientAngle { get; }
            public RangeMetrics PotentialRange { get; }
            public RangeMetrics AdjointRange { get; }
            public RangeMetrics RegularizationRange { get; }
            public double RegularizationEnergy { get; }
            public Dictionary<int, double>? GradientSnapshot { get; }
        }

        private readonly struct RangeMetrics
        {
            public static RangeMetrics Empty { get; } = new RangeMetrics(null, null);

            public RangeMetrics(double? min, double? max)
            {
                Min = min;
                Max = max;
            }

            public double? Min { get; }
            public double? Max { get; }
            public bool HasValue => Min.HasValue && Max.HasValue;
        }

        public void StartBackgroundReconstruction()
        {
            PrepareForNewReconstruction();
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
            _lastRunSignature = null;
        }

        /// <summary>
        /// Completely resets all reconstruction-related data and deallocates resources.
        /// This clears all reconstruction results, frames, and resets the reconstruction state.
        /// </summary>
        public void RestartReconstruction()
        {
            StopReconstruction();
            // Reset view model state and clear workspace frames/results
            Workspace.ClearReconstructionFrames();
            Workspace.SetReconstructionResults(new List<ReconstructionResult>());

            IterationCount = 0;
            Residual = 1.0;
            Correlation = 0.0;
            ElapsedTime = TimeSpan.Zero;
        }

        public void ResetAllToDefaults()
        {
            var defaults = new EITReconstructionParameters();
            ReconstructionParameters = defaults;

            MaxIterationCount = 50;
            StepSize = 0.001;
            RegularizationWeight = 0.001;

            ExcitationElectrodeId = 1;
            GroundElectrodeId = 0;
            SetDrivePattern(DrivePattern.Adjecent);
        }

        public Task<ReconstructionResult?> RunFullReconstructionCycleAsync()
        {
            InitializeReconstruction();
            BeginReconstructionMetrics();
            var result = _reconstructionService.RunFullReconstructionCycleAsync(StepSize,
                                                                         RegularizationWeight,
                                                                         ExcitationCurrentAmplitude);
            StopElapsedTimer();

            return result;
        }

        public async Task StepReconstructionAsync()
        {
            InitializeReconstruction();
            BeginReconstructionMetrics();
            await _reconstructionService.StepReconstructionAsync();
            StopElapsedTimer();
            FlushPendingTrendUpdates();
        }

        // Event handlers wired to the service
        private void OnServiceReconstructionUpdated(object? sender, ReconstructionResult result)
        {
            ScheduleReconstructionStatisticsUpdate(result);
        }

        private void OnServiceFrameUpdated(object? sender, ReconstructionFrame frame)
        {
            RequestMetricUpdate(null, frame);
            ReconstructionFrameUpdated?.Invoke(this, frame);
        }

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

        public void PrepareVideoExportOptions(SKSize distributionCanvasSize,
                                              SKSize colorbarCanvasSize,
                                              SKSize residualCanvasSize,
                                              PotentialDisplayMode mode)
        {
            ResetVideoExportState();

            _ = mode;

            _videoExportDistributionSize = ReconstructionVideoRenderer.NormalizeSize(distributionCanvasSize, 250, 250);
            _videoExportColorbarSize = ReconstructionVideoRenderer.NormalizeSize(colorbarCanvasSize, 250, 20);
            _videoExportResidualSize = ReconstructionVideoRenderer.NormalizeSize(residualCanvasSize, 600, 170);
            _videoExportFrameSize = ReconstructionVideoRenderer.CalculateFrameDimensions(_videoExportDistributionSize,
                                                                                         _videoExportColorbarSize,
                                                                                         _videoExportResidualSize);

            var frames = Workspace.GetReconstructionFrames();
            _videoExportFrameCount = frames.Count;

            VideoExportFormatOptions.Clear();
            VideoExportFormatOptions.Add(new VideoExportFormatOption("MP4", "Best balance of quality and size.", VideoExportContainer.Mp4, ".mp4"));
            VideoExportFormatOptions.Add(new VideoExportFormatOption("AVI", "Larger files but widely supported.", VideoExportContainer.Avi, ".avi"));

            VideoExportHeading = "Export Reconstruction Video";
            VideoExportStatusMessage = _videoExportFrameCount > 0
                ? "Choose the output format for your reconstruction video."
                : "No reconstruction frames are available for export.";

            SelectedVideoExportFormat = VideoExportFormatOptions.FirstOrDefault();
            UpdateVideoExportEstimates();
            UpdateVideoExportPhase();
        }

        public async Task<VideoExportResult> ExportReconstructionVideoAsync(SKSize distributionCanvasSize,
                                                                            SKSize colorbarCanvasSize,
                                                                            SKSize residualCanvasSize,
                                                                            PotentialDisplayMode mode,
                                                                            VideoExportContainer container,
                                                                            IProgress<VideoExportProgressReport>? progress = null,
                                                                            CancellationToken cancellationToken = default)
        {
            var frames = Workspace.GetReconstructionFrames().ToList();
            if (frames.Count == 0)
                return VideoExportResult.CreateFailure("No Frames", "There are no reconstruction frames to export.");

            var discretization = Workspace.GetDiscretization();
            if (discretization == null)
                return VideoExportResult.CreateFailure("No Mesh", "Unable to determine the discretization for rendering.");

            progress?.Report(new VideoExportProgressReport(0.0,
                                                            "Preparing reconstruction frames for video generation..."));

            var results = Workspace.GetReconstructionResults().ToList();
            var residualHistory = results
                .Select(CalculateResidual)
                .ToList();

            var distributionSize = ReconstructionVideoRenderer.NormalizeSize(distributionCanvasSize, 250, 250);
            var colorbarSize = ReconstructionVideoRenderer.NormalizeSize(colorbarCanvasSize, 250, 20);
            var residualSize = ReconstructionVideoRenderer.NormalizeSize(residualCanvasSize, 600, 170);

            string directory = FileSystem.Current.AppDataDirectory;
            Directory.CreateDirectory(directory);
            string baseFileName = $"reconstruction_{DateTime.Now:yyyyMMdd_HHmmss}";
            string mp4FilePath = Path.Combine(directory, baseFileName + ".mp4");
            string aviFallbackFilePath = Path.Combine(directory, baseFileName + ".avi");
            string requestedFilePath = container switch
            {
                VideoExportContainer.Avi => aviFallbackFilePath,
                _ => mp4FilePath
            };
            string? finalFilePath = null;

            try
            {
                await Task.Run(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int videoWidth = 0;
                    int videoHeight = 0;
                    double totalSteps = frames.Count + 1.0;
                    string tempFrameDirectory = Path.Combine(FileSystem.Current.CacheDirectory,
                                                             "VideoExportFrames",
                                                             Guid.NewGuid().ToString("N"));

                    Directory.CreateDirectory(tempFrameDirectory);

                    var encodedFrames = new List<byte[]>(frames.Count);
                    var frameImagePaths = new List<string>(frames.Count);

                    try
                    {
                        for (int i = 0; i < frames.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            double progressValue = (i + 1) / totalSteps;
                            progress?.Report(new VideoExportProgressReport(progressValue,
                                                                            $"Rendering frame {i + 1} of {frames.Count}..."));

                            var context = ReconstructionVideoRenderer.FindResultForFrame(results, i, out int resultIndex);
                            int residualCount = resultIndex >= 0
                                ? Math.Min(resultIndex + 1, residualHistory.Count)
                                : residualHistory.Count;

                            using var image = ReconstructionVideoRenderer.RenderFrameSnapshot(frames[i],
                                                                                               context,
                                                                                               discretization,
                                                                                               residualHistory,
                                                                                               residualCount,
                                                                                               distributionSize,
                                                                                               colorbarSize,
                                                                                               residualSize,
                                                                                               mode);

                            if (videoWidth == 0 || videoHeight == 0)
                            {
                                videoWidth = image.Width;
                                videoHeight = image.Height;
                            }
                            else if (image.Width != videoWidth || image.Height != videoHeight)
                            {
                                throw new InvalidOperationException("All exported frames must share the same dimensions.");
                            }

                            using var encodedFrame = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                            if (encodedFrame == null)
                                throw new InvalidOperationException("Failed to encode video frame to JPEG.");

                            var frameBytes = encodedFrame.ToArray();
                            encodedFrames.Add(frameBytes);

                            string framePath = Path.Combine(tempFrameDirectory, $"frame_{i:D6}.jpg");
                            await File.WriteAllBytesAsync(framePath, frameBytes, cancellationToken).ConfigureAwait(false);
                            frameImagePaths.Add(framePath);
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        progress?.Report(new VideoExportProgressReport(
                            Math.Min(0.98, frames.Count / (frames.Count + 1.0)),
                            "Encoding video stream..."));

                        bool mp4Created = false;

                        if (container != VideoExportContainer.Avi)
                        {
                            mp4Created = await Mp4VideoExporter.TryExportAsync(frameImagePaths,
                                                                              videoWidth,
                                                                              videoHeight,
                                                                              Workspace.ReconstructionVideoFramesPerSecond,
                                                                              requestedFilePath,
                                                                              cancellationToken).ConfigureAwait(false);

                            if (mp4Created)
                            {
                                finalFilePath = requestedFilePath;
                            }
                        }

                        if (!mp4Created)
                        {
                            if (encodedFrames.Count == 0)
                                throw new InvalidOperationException("No frames were encoded for export.");

                            if (File.Exists(mp4FilePath) && container != VideoExportContainer.Avi)
                            {
                                try
                                {
                                    File.Delete(mp4FilePath);
                                }
                                catch
                                {
                                    // Ignore failures when cleaning up a partial MP4 export.
                                }
                            }

                            string aviTargetPath = container == VideoExportContainer.Avi
                                ? requestedFilePath
                                : aviFallbackFilePath;

                            if (File.Exists(aviTargetPath))
                            {
                                try
                                {
                                    File.Delete(aviTargetPath);
                                }
                                catch
                                {
                                    // Ignore cleanup errors.
                                }
                            }

                            using var stream = File.Create(aviTargetPath);
                            using var videoStream = AviVideoWriter.BeginWrite(stream,
                                                                             videoWidth,
                                                                             videoHeight,
                                                                             Workspace.ReconstructionVideoFramesPerSecond,
                                                                             encodedFrames[0].Length,
                                                                             AviVideoWriter.AviVideoCodec.MotionJpeg);

                            foreach (var frame in encodedFrames)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                videoStream.WriteFrame(frame);
                            }

                            videoStream.Complete();
                            finalFilePath = aviTargetPath;
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (Directory.Exists(tempFrameDirectory))
                            {
                                Directory.Delete(tempFrameDirectory, recursive: true);
                            }
                        }
                        catch
                        {
                            // Ignore cleanup errors.
                        }
                    }
                }, cancellationToken);

                progress?.Report(new VideoExportProgressReport(1.0, "Video generation completed."));
                string path = finalFilePath ?? requestedFilePath;
                return VideoExportResult.CreateSuccess(path);
            }
            catch (OperationCanceledException)
            {
                foreach (var path in new[] { mp4FilePath, aviFallbackFilePath })
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch
                        {
                            // Ignore any errors encountered during cleanup.
                        }
                    }
                }

                return VideoExportResult.CreateFailure("Export Aborted", "The video export was aborted.");
            }
            catch (Exception ex)
            {
                foreach (var path in new[] { mp4FilePath, aviFallbackFilePath })
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch
                        {
                            // Ignore cleanup errors when reporting the failure.
                        }
                    }
                }

                return VideoExportResult.CreateFailure("Export Failed", ex.Message);
            }
        }

        public Task<DataExportResult> ExportReconstructionDataAsync(PotentialDisplayMode mode)
        {
            string rootDirectory = FileSystem.Current.AppDataDirectory;
            return _exportService.ExportAsync(rootDirectory, mode);
        }

        private static DistributionMetrics? ComputeInitialDistributionMetrics(ReconstructionResult snapshot)
        {
            var initialResult = new ReconstructionResult(snapshot.OriginalConductivityDistribution,
                                                         snapshot.InitialConductivitiyDistribution,
                                                         snapshot.InitialConductivitiyDistribution,
                                                         snapshot.Frames);
            return ComputeDistributionMetrics(initialResult, CancellationToken.None);
        }

        private static ReconstructionFrame GetFrameForResult(ReconstructionResult result, ReconstructionFrame fallback)
            => result.Frames.LastOrDefault() ?? fallback;

        public void BeginVideoExportProgress()
        {
            VideoExportIsRunning = true;
            VideoExportHasResult = false;
            VideoExportWasSuccessful = false;
            VideoExportHeading = "Video Generation in Progress";
            VideoExportStatusMessage = "Preparing video generation...";
            UpdateVideoExportProgressCore(0.0);
            VideoExportFilePath = null;
            VideoExportResult = null;
            UpdateVideoExportCanStart();
        }

        public void UpdateVideoExportProgress(VideoExportProgressReport report)
        {
            UpdateVideoExportProgressCore(report.Progress);
            VideoExportStatusMessage = report.StatusMessage;
        }

        public void NotifyVideoExportAborting()
        {
            VideoExportStatusMessage = "Aborting video generation...";
        }

        public void CompleteVideoExport(VideoExportResult result)
        {
            VideoExportIsRunning = false;
            VideoExportHasResult = true;
            VideoExportWasSuccessful = result.Success;
            VideoExportResult = result;

            if (result.Success)
            {
                VideoExportHeading = "Video Generated Successfully";
                VideoExportStatusMessage = "Your reconstruction video is ready.";
                VideoExportFilePath = result.FilePath;
                UpdateVideoExportProgressCore(1.0);
            }
            else
            {
                VideoExportHeading = result.ErrorTitle ?? "Export Failed";
                VideoExportStatusMessage = result.ErrorMessage ?? "An unknown error occurred during video export.";
                VideoExportFilePath = null;
            }

            UpdateVideoExportPhase();
        }

        public void ResetVideoExportState()
        {
            VideoExportIsRunning = false;
            VideoExportHasResult = false;
            VideoExportWasSuccessful = false;
            VideoExportHeading = "Video Generation in Progress";
            VideoExportStatusMessage = string.Empty;
            UpdateVideoExportProgressCore(0.0);
            VideoExportFilePath = null;
            VideoExportResult = null;
            VideoExportFormatOptions.Clear();
            SelectedVideoExportFormat = null;
            VideoExportEstimatedSizeText = string.Empty;
            VideoExportEstimatedTimeText = string.Empty;
            _videoExportFrameCount = 0;
            _videoExportFrameSize = new SKSizeI(0, 0);
            UpdateVideoExportPhase();
        }

        private void UpdateVideoExportProgressCore(double progress)
        {
            double clamped = Math.Clamp(progress, 0.0, 1.0);
            VideoExportProgress = clamped;
            VideoExportProgressPercentText = $"{Math.Round(clamped * 100.0)}%";
        }

        private void UpdateVideoExportEstimates()
        {
            if (SelectedVideoExportFormat is null ||
                _videoExportFrameCount <= 0 ||
                _videoExportFrameSize.Width <= 0 ||
                _videoExportFrameSize.Height <= 0)
            {
                VideoExportEstimatedSizeText = "Estimated size: --";
                VideoExportEstimatedTimeText = "Estimated time: --";
                UpdateVideoExportCanStart();
                return;
            }

            double pixelCount = (double)_videoExportFrameSize.Width * _videoExportFrameSize.Height;
            double bytesPerPixel = SelectedVideoExportFormat.Container switch
            {
                VideoExportContainer.Mp4 => 0.09,
                VideoExportContainer.Avi => 0.16,
                _ => 0.1
            };

            double totalBytes = pixelCount * _videoExportFrameCount * bytesPerPixel;
            double sizeMb = totalBytes / (1024.0 * 1024.0);
            VideoExportEstimatedSizeText = $"Estimated size: ~{sizeMb:F1} MB";

            double baseSecondsPerFrame = SelectedVideoExportFormat.Container switch
            {
                VideoExportContainer.Mp4 => 0.12,
                VideoExportContainer.Avi => 0.09,
                _ => 0.1
            };

            double complexityFactor = Math.Max(1.0, pixelCount / 400_000.0);
            double estimatedSeconds = Math.Max(2.0, _videoExportFrameCount * baseSecondsPerFrame * complexityFactor);
            VideoExportEstimatedTimeText = $"Estimated time: ~{estimatedSeconds:F1} s";

            UpdateVideoExportCanStart();
        }

        private void UpdateVideoExportPhase()
        {
            VideoExportIsConfiguring = !VideoExportIsRunning && !VideoExportHasResult;
            UpdateVideoExportCanStart();
        }

        private void UpdateVideoExportCanStart()
        {
            bool canStart = !VideoExportIsRunning
                            && !VideoExportHasResult
                            && SelectedVideoExportFormat is not null
                            && _videoExportFrameCount > 0;

            VideoExportCanStart = canStart;
        }

        private void ApplyReconstructionFilter()
        {
            FilteredReconstructions.Clear();
            foreach (var r in AvailableReconstructions.Where(r =>
                         string.IsNullOrWhiteSpace(ReconstructionSearchText) ||
                         r.Name.Contains(ReconstructionSearchText, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredReconstructions.Add(r);
            }
        }

        private ReconstructionRunSignature CreateCurrentRunSignature(IDiscretization mesh)
        {
            var parameters = ReconstructionParameters;
            var snapshot = new ReconstructionParametersSnapshot(
                parameters.DifferentialEquationSolver,
                parameters.RegularizationTechnique,
                parameters.ErrorMetric,
                parameters.NumericSolver,
                parameters.NumericOptimizer,
                parameters.InitialDistributionType,
                parameters.MeasurementNoiseType,
                parameters.MeasurementNoiseAmplitude,
                RegularizationWeight,
                ExcitationCurrentAmplitude,
                ExcitationElectrodeId,
                GroundElectrodeId,
                parameters.DrivePattern,
                parameters.UsePotentialDifferences,
                parameters.UseOmpParallelization,
                parameters.UseCudaAcceleration);

            return new ReconstructionRunSignature(mesh, snapshot);
        }

        private ObservableCollection<double> GetTrendHistory(string key)
        {
            if (!_metricTrendHistories.TryGetValue(key, out var history))
            {
                history = new ObservableCollection<double>();
                _metricTrendHistories[key] = history;
            }

            return history;
        }

        private void AddTrendSample(string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            var history = GetTrendHistory(key);
            history.Add(value);
            NotifyTrendHistoryChanged(key);
        }

        private void ScheduleReconstructionStatisticsUpdate(ReconstructionResult result)
        {
            CancellationTokenSource cts;
            lock (_statisticsUpdateLock)
            {
                _statisticsUpdateCts?.Cancel();
                _statisticsUpdateCts?.Dispose();
                _statisticsUpdateCts = new CancellationTokenSource();
                cts = _statisticsUpdateCts;
            }

            _ = Task.Run(() => ProcessReconstructionStatisticsAsync(result, cts), cts.Token);
        }

        private async Task ProcessReconstructionStatisticsAsync(ReconstructionResult result, CancellationTokenSource cts)
        {
            try
            {
                var token = cts.Token;
                double residual = CalculateResidual(result);
                token.ThrowIfCancellationRequested();

                double correlation = CalculateCorrelation(result.ReconstructedConductivityDistribution,
                                                          result.OriginalConductivityDistribution);
                token.ThrowIfCancellationRequested();

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    Residual = residual;
                    Correlation = correlation;
                    ElapsedTime = _reconstructionStopwatch.Elapsed;
                    IterationCount++;
                    AddTrendSample(MetricKeys.Residual, Residual);
                    AddTrendSample(MetricKeys.Correlation, Correlation);
                    UpdateResidualTrendMetrics();
                    RequestMetricUpdate(result, null);

                    if (IterationCount % 10 == 0)
                        MainThread.BeginInvokeOnMainThread(() => ReconstructionUpdated?.Invoke(this, result));
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reconstruction statistics computation failed: {ex}");
            }
            finally
            {
                lock (_statisticsUpdateLock)
                {
                    if (ReferenceEquals(_statisticsUpdateCts, cts))
                    {
                        _statisticsUpdateCts.Dispose();
                        _statisticsUpdateCts = null;
                    }
                }
            }
        }

        private void NotifyTrendHistoryChanged(string key)
        {
            if (key != SelectedTrendMetricKey)
                return;

            if (IterationCount == 0 || IterationCount % TrendCanvasUpdateInterval == 0)
            {
                _hasPendingTrendUpdate = false;
                RaiseSelectedTrendMetricHistoryChanged();
            }
            else
            {
                _hasPendingTrendUpdate = true;
            }
        }

        private void RaiseSelectedTrendMetricHistoryChanged()
        {
            MainThread.BeginInvokeOnMainThread(() => SelectedTrendMetricHistoryChanged?.Invoke(this, EventArgs.Empty));
        }

        private void FlushPendingTrendUpdates()
        {
            if (!_hasPendingTrendUpdate)
                return;

            _hasPendingTrendUpdate = false;
            RaiseSelectedTrendMetricHistoryChanged();
        }

        private void UpdateTrendSelectionStates()
        {
            foreach (var metric in _metricsByKey.Values)
                metric.IsTrendSelected = metric.Key == SelectedTrendMetricKey;
        }

        partial void OnSelectedTrendMetricKeyChanged(string value)
        {
            UpdateTrendSelectionStates();
            _hasPendingTrendUpdate = false;
            RaiseSelectedTrendMetricHistoryChanged();
        }

        [RelayCommand]
        private void SelectTrendMetric(string metricKey)
        {
            if (!_metricsByKey.TryGetValue(metricKey, out var metric) || !metric.IsTrendSelectable)
                return;

            SelectedTrendMetricKey = metricKey;
        }

        [RelayCommand]
        private void RequestGradientInspection(string metricKey)
        {
            if (string.IsNullOrWhiteSpace(metricKey))
                return;

            if (metricKey == MetricKeys.GradientNorm
                || metricKey == MetricKeys.GradientAngleChange
                || metricKey == MetricKeys.PotentialRange
                || metricKey == MetricKeys.AdjointRange)
            {
                GradientInspectionRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        public sealed class GradientHistorySample
        {
            private readonly double[] _vector;

            public GradientHistorySample(int iteration,
                                         double[] vector,
                                         double norm,
                                         int frameIndex,
                                         double? angle)
            {
                Iteration = iteration;
                _vector = vector;
                Norm = norm;
                FrameIndex = frameIndex;
                Angle = angle;
            }

            public int Iteration { get; }
            public double Norm { get; }
            public int FrameIndex { get; }
            public double? Angle { get; }
            public IReadOnlyList<double> Vector => Array.AsReadOnly(_vector);

            internal double[] GetVectorCopy() => (double[])_vector.Clone();
        }

        private readonly record struct ReconstructionParametersSnapshot(
            DifferentialEquationSolver DifferentialEquationSolver,
            RegularizationTechnique RegularizationTechnique,
            ErrorMetric ErrorMetric,
            NumericSolver NumericSolver,
            NumericOptimizer NumericOptimizer,
            InitialDistributionTypes InitialDistributionType,
            MeasurementNoiseType MeasurementNoiseType,
            double MeasurementNoiseAmplitude,
            double RegularizationWeight,
            double ExcitationCurrentAmplitude,
            int ExcitationElectrodeId,
            int GroundElectrodeId,
            DrivePattern DrivePattern,
            bool UsePotentialDifferences,
            bool UseOmpParallelization,
            bool UseCudaAcceleration);

        private record ReconstructionRunSignature(IDiscretization Mesh, ReconstructionParametersSnapshot Parameters);
    }

    public sealed partial class ReconstructionMetricGroupViewModel : ObservableObject
    {
        public ReconstructionMetricGroupViewModel(string title)
        {
            Title = title;
        }

        public string Title { get; }

        public ObservableCollection<ReconstructionMetricViewModel> Metrics { get; } = [];
    }

    public sealed partial class ReconstructionMetricViewModel : ObservableObject
    {
        public ReconstructionMetricViewModel(string key, string name, TrendMetricCategory trendCategory)
        {
            Key = key;
            Name = name;
            TrendCategory = trendCategory;
            IsTrendSelectable = trendCategory != TrendMetricCategory.None;
        }

        public string Key { get; }
        public string Name { get; }
        public TrendMetricCategory TrendCategory { get; }
        public bool IsTrendSelectable { get; }

        [ObservableProperty]
        private string value = "—";

        [ObservableProperty]
        private bool isTrendSelected;
    }

    public enum TrendMetricCategory
    {
        None,
        Residual,
        ErrorNorm,
        Similarity
    }
}