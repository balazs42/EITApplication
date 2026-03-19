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
using Utility.Classes.Configurations.ReconstructionConfiguration;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;
using Utility.Rendering;

using Workspace = Utility.Classes.Application.Workspace;
using Timer = System.Timers.Timer;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes.Reconstruction.Metrics;

namespace ElectricalImpedanceTomography.ViewModels
{
    /// <summary>
    /// Orchestrates EIT reconstruction workflows from the UI perspective.
    /// 
    /// The view model exposes commands and observable properties to drive forward and
    /// inverse solves, coordinates both the classic and block-based reconstruction paths,
    /// collects metrics, and pushes frame/result notifications back to the view.
    /// </summary>
    public partial class ReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        /// <summary>Facade for the classic (non‑block) reconstruction orchestration.</summary>
        private readonly IReconstructionService _reconstructionService;
       
        /// <summary>Facade for the experimental block‑based FEM reconstruction pipeline.</summary>
        private readonly IBlockFemReconstructionService _blockReconstructionService;
        
        /// <summary>Service for exporting videos/data derived from reconstruction frames.</summary>
        private readonly IReconstructionExportService _exportService;

        /// <summary>Wall‑clock stopwatch for elapsed time and performance metrics.</summary>
        private readonly Stopwatch _reconstructionStopwatch = new();
        
        /// <summary>Periodic UI timer that refreshes the <see cref="ElapsedTime"/> binding.</summary>
        private readonly Timer _elapsedTimer;

        /// <summary>Indicates if the block pipeline was initialized for the active configuration.</summary>
        private bool _blockInitialized;
        
        /// <summary>Caches the last block configuration to detect re‑initialization boundaries.</summary>
        private CompleteReconstructionConfiguration? _lastBlockConfiguration;

        /// <summary>Active discretization reference from the <see cref="Workspace"/>.</summary>
        private IDiscretization? _discretization = Workspace.GetDiscretization();
        
        /// <summary>Tracks the discretization instance used when the persistence layer was last initialized.</summary>
        private IDiscretization? _initializedDiscretization;
        
        /// <summary>Captures the invariant run signature used to detect parameter changes between runs.</summary>
        private ReconstructionRunSignature? _lastRunSignature;
        
        /// <summary>When true, trend histories are cleared on the next Begin operation.</summary>
        private bool _resetMetricsOnStart = true;
        
        /// <summary>Prevents feedback loops while synchronizing drive pattern picker state.</summary>
        private bool _updatingDrivePatternSelection;
        
        /// <summary>Tracks reconstruction parameters for change detection and UI sync.</summary>
        private ReconstructionRuntimeContext? _trackedParameters;
        
        /// <summary>Current measurement source selection mirrored from the workspace.</summary>
        private MeasurementSourceOption _selectedMeasurementSource = Workspace.GetMeasurementSource();

        /// <summary>Whether intermediate reconstruction frames should be visualised.</summary>
        [ObservableProperty]
        private bool visualizeIterations = true;

        /// <summary>Current potential render mode for video export.</summary>
    private PotentialDisplayMode _selectedPotentialDisplayMode = PotentialDisplayMode.Default;
    private ConductivityDisplayMode _selectedConductivityDisplayMode = ConductivityDisplayMode.Classic;

        /// <summary>Shortcut to virtual electrode settings from the global parameters.</summary>
        public VirtualElectrodeSettings VirtualElectrodeSettings => ReconstructionParameters.VirtualElectrodeSettings;

        /// <summary>Enumeration source for the Virtual Electrode method picker.</summary>
        public IEnumerable<VirtualElectrodeMethod> VirtualElectrodeMethods { get; } = Enum.GetValues<VirtualElectrodeMethod>();

        /// <summary>True when the Linear Combination virtual electrode method is active.</summary>
        public bool IsLinearCombinationMethod => VirtualElectrodeSettings.UseVirtualElectrodes && VirtualElectrodeSettings.Method == VirtualElectrodeMethod.LinearCombination;
        /// <summary>True when the Harrach sensitivity interpolation method is active.</summary>
        public bool IsHarrachMethod => VirtualElectrodeSettings.UseVirtualElectrodes && VirtualElectrodeSettings.Method == VirtualElectrodeMethod.HarrachSensitivityInterpolation;
        /// <summary>True when the NdMap spectral interpolation method is active.</summary>
        public bool IsNdMethod => VirtualElectrodeSettings.UseVirtualElectrodes && VirtualElectrodeSettings.Method == VirtualElectrodeMethod.NdMapSpectralInterpolation;

        /// <summary>Tracks the last initial distribution type applied to the workspace.</summary>
        private InitialDistributionTypes _lastAppliedInitialDistributionType;

        private const string MixedPickerLabel = "Mixed";

        /// <summary>
        /// Whether the view should honour the block configuration canvas for method selection.
        /// When enabled and a configuration is present, pickers are synced to the configured blocks.
        /// </summary>
        [ObservableProperty]
        private bool useBlockConfiguration = Workspace.GetUseBlockConfiguration();

        /// <summary>Options displayed in the Error Metric picker (or the special "Mixed" label).</summary>
        public ObservableCollection<string> ErrorMetricPickerOptions { get; } = new();
        /// <summary>Options displayed in the Regularization picker (or the special "Mixed" label).</summary>
        public ObservableCollection<string> RegularizationPickerOptions { get; } = new();
        /// <summary>Options displayed in the Optimizer picker (or the special "Mixed" label).</summary>
        public ObservableCollection<string> OptimizerPickerOptions { get; } = new();

        /// <summary>Currently selected error metric option (or "Mixed").</summary>
        [ObservableProperty]
        private string? selectedErrorMetricDisplay;

        /// <summary>Currently selected regularizer option (or "Mixed").</summary>
        [ObservableProperty]
        private string? selectedRegularizationDisplay;

        /// <summary>Currently selected optimizer option (or "Mixed").</summary>
        [ObservableProperty]
        private string? selectedOptimizerDisplay;

        /// <summary>Enables or disables the Error Metric picker; disabled when configuration is mixed.</summary>
        [ObservableProperty]
        private bool isErrorMetricPickerEnabled = true;

        /// <summary>Enables or disables the Regularization picker; disabled when configuration is mixed.</summary>
        [ObservableProperty]
        private bool isRegularizationPickerEnabled = true;

        /// <summary>Enables or disables the Optimizer picker; disabled when configuration is mixed.</summary>
        [ObservableProperty]
        private bool isOptimizerPickerEnabled = true;

        /// <summary>True when a block configuration with multiple blocks per category is in effect.</summary>
        private bool ShouldUseBlockConfiguration => UseBlockConfiguration && Workspace.GetCompleteReconstructionConfiguration() != null;

        /// <summary>
        /// Propagates the "use block configuration" toggle to the workspace and resets picker state.
        /// </summary>
        /// <param name="value">New toggle value.</param>
        partial void OnUseBlockConfigurationChanged(bool value)
        {
            Workspace.SetUseBlockConfiguration(value);
            _blockInitialized = false;
            RefreshMethodPickerOptions();
        }

        /// <summary>Updates the underlying parameters when the error metric picker changes.</summary>
        partial void OnSelectedErrorMetricDisplayChanged(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == MixedPickerLabel)
                return;

            if (Enum.TryParse<ErrorMetric>(value, out var parsed))
                ReconstructionParameters.ErrorMetric = parsed;
        }

        /// <summary>Updates the underlying parameters when the regularization picker changes.</summary>
        partial void OnSelectedRegularizationDisplayChanged(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == MixedPickerLabel)
                return;

            if (Enum.TryParse<RegularizationTechnique>(value, out var parsed))
                ReconstructionParameters.RegularizationTechnique = parsed;
        }

        /// <summary>Updates the underlying parameters when the optimizer picker changes.</summary>
        partial void OnSelectedOptimizerDisplayChanged(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == MixedPickerLabel)
                return;

            if (Enum.TryParse<NumericOptimizer>(value, out var parsed))
                ReconstructionParameters.NumericOptimizer = parsed;
        }

        /// <summary>Propagates the visualization toggle to the services so they can throttle frame events.</summary>
        partial void OnVisualizeIterationsChanged(bool value)
        {
            _reconstructionService.VisualizeIterations = value;
            _blockReconstructionService.VisualizeIterations = value;
        }

        /// <summary>Total number of completed iterations in the current session.</summary>
        [ObservableProperty]
        private int iterationCount = 0;

        /// <summary>Allows editing the initial distribution only before the first iteration.</summary>
        public bool CanEditInitialDistribution => IterationCount == 0;

        /// <summary>Latest residual value (L2 norm unless configured otherwise).</summary>
        [ObservableProperty]
        private double residual = 1.0;
        /// <summary>Elapsed wall‑clock time since the current run started.</summary>
        [ObservableProperty]
        private TimeSpan elapsedTime = TimeSpan.Zero;
        /// <summary>Latest Pearson correlation between reconstructed and original distributions.</summary>
        [ObservableProperty]
        private double correlation = 0.0;

        /// <summary>Two‑state UI that mirrors the adjacent/skip-x drive pattern selection.</summary>
        [ObservableProperty]
        private bool adjecentDrivePattern = true;
        /// <summary>Two‑state UI that mirrors the Opposite drive pattern selection.</summary>
        [ObservableProperty]
        private bool oppositeDrivePattern = false;

        /// <summary>Text shown next to the parallelisation toggle, dynamically reflecting the solver.</summary>
        [ObservableProperty]
        private string parallelizationToggleLabel = "Use OMP Parallelization";

        /// <summary>Indicates that a background reconstruction loop is running to gate UI actions.</summary>
        [ObservableProperty]
        private bool isReconstructionRunning;

        /// <summary>
        /// Single toggle that maps to OMP (FEM) or CUDA (LBM) depending on the selected solver.
        /// </summary>
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

        /// <summary>True when an imported (real) measurement is available as a selectable source.</summary>
        public bool HasMeasurementSourceOptions => Workspace.GetImportedMeasurement() != null;

        /// <summary>Convenience binding for the source selection radio button (Simulated).</summary>
        public bool IsSimulatedMeasurementSelected
        {
            get => SelectedMeasurementSource == MeasurementSourceOption.Simulated;
            set
            {
                if (value)
                    SelectedMeasurementSource = MeasurementSourceOption.Simulated;
            }
        }

        /// <summary>Convenience binding for the source selection radio button (Real).</summary>
        public bool IsRealMeasurementSelected
        {
            get => SelectedMeasurementSource == MeasurementSourceOption.Real;
            set
            {
                if (value)
                    SelectedMeasurementSource = MeasurementSourceOption.Real;
            }
        }

        /// <summary>Label shown for the "Real" measurement option; includes the imported file name when available.</summary>
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

        /// <summary>Textual description of the electrode measurement setup (Active vs Non‑active).</summary>
        public string ElectrodeMeasurementSetupLabel
        {
            get => _electrodeMeasurementSetupLabel;
            private set => SetProperty(ref _electrodeMeasurementSetupLabel, value);
        }

        /// <summary>Formats the measurement setup label.</summary>
        private static string FormatMeasurementSetupLabel(ElectrodeMeasurementSetup setup) => setup == ElectrodeMeasurementSetup.Active
            ? "Electrode measurement setup: Active (excitation electrodes are sampled)"
            : "Electrode measurement setup: Non-active (excitation electrodes are ignored)";

        /// <summary>Updates the measurement setup label when the workspace raises a change event.</summary>
        private void OnElectrodeMeasurementSetupChanged(ElectrodeMeasurementSetup setup)
        {
            ElectrodeMeasurementSetupLabel = FormatMeasurementSetupLabel(setup);
        }

        /// <summary>Refreshes the radio selection state for measurement source.</summary>
        public void RefreshMeasurementSourceOptions() => RefreshMeasurementSourceSelection();

        /// <summary>
        /// Internal backing property for measurement source that enforces fallbacks when real data is absent.
        /// </summary>
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

        /// <summary>
        /// Synchronizes the binding state for the measurement source and the measurement setup label with workspace.
        /// </summary>
        private void RefreshMeasurementSourceSelection()
        {
            if (!HasMeasurementSourceOptions && _selectedMeasurementSource == MeasurementSourceOption.Real)
                Workspace.SetMeasurementSource(MeasurementSourceOption.Simulated);

            _selectedMeasurementSource = Workspace.GetMeasurementSource();
            OnPropertyChanged(nameof(HasMeasurementSourceOptions));
            OnPropertyChanged(nameof(RealMeasurementOptionLabel));
            OnPropertyChanged(nameof(IsSimulatedMeasurementSelected));
            OnPropertyChanged(nameof(IsRealMeasurementSelected));
            ElectrodeMeasurementSetupLabel = FormatMeasurementSetupLabel(Workspace.GetElectrodeMeasurementSetup());
        }

        /// <summary>
        /// Re‑computes UI toggles when virtual electrode settings change so controls can show/hide sections.
        /// </summary>
        private void OnVirtualElectrodeSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VirtualElectrodeSettings.Method) ||
                e.PropertyName == nameof(VirtualElectrodeSettings.UseVirtualElectrodes) ||
                string.IsNullOrEmpty(e.PropertyName))
            {
                OnPropertyChanged(nameof(IsLinearCombinationMethod));
                OnPropertyChanged(nameof(IsHarrachMethod));
                OnPropertyChanged(nameof(IsNdMethod));
            }
        }

        /// <summary>User-specified friendly name for persisted reconstructions.</summary>
        [ObservableProperty]
        private string name = string.Empty;
        /// <summary>Search term used to filter stored reconstructions by name.</summary>
        [ObservableProperty]
        private string reconstructionSearchText = string.Empty;

        /// <summary>True while a video export job is being processed.</summary>
        [ObservableProperty]
        private bool videoExportIsRunning;
        
        /// <summary>True when the last export produced a result (success or failure).</summary>
        [ObservableProperty]
        private bool videoExportHasResult;
        
        /// <summary>True when the last export finished successfully.</summary>
        [ObservableProperty]
        private bool videoExportWasSuccessful;
        
        /// <summary>True when the video export UI is in configuration (pre‑run) phase.</summary>
        [ObservableProperty]
        private bool videoExportIsConfiguring;
        
        /// <summary>Heading line shown in the video export popup.</summary>
        [ObservableProperty]
        private string videoExportHeading = "Video Generation in Progress";
        
        /// <summary>Status line shown in the video export popup.</summary>
        [ObservableProperty]
        private string videoExportStatusMessage = string.Empty;
        
        /// <summary>Progress value in [0,1] for the video export operation.</summary>
        [ObservableProperty]
        private double videoExportProgress;
        
        /// <summary>Progress string (percent) shown in the export popup.</summary>
        [ObservableProperty]
        private string videoExportProgressPercentText = "0%";
        
        /// <summary>Absolute file path of the last generated video file, when successful.</summary>
        [ObservableProperty]
        private string? videoExportFilePath;
        
        /// <summary>Rich result object exposing error details or success metadata.</summary>
        [ObservableProperty]
        private VideoExportResult? videoExportResult;
        
        /// <summary>User-selected container/format for the export run.</summary>
        [ObservableProperty]
        private VideoExportFormatOption? selectedVideoExportFormat;
        
        /// <summary>Human‑readable estimate of file size for the selected export configuration.</summary>
        [ObservableProperty]
        private string videoExportEstimatedSizeText = string.Empty;
        
        /// <summary>Human‑readable estimate of processing time for the selected export configuration.</summary>
        [ObservableProperty]
        private string videoExportEstimatedTimeText = string.Empty;
        
        /// <summary>True when inputs are valid and an export can be started.</summary>
        [ObservableProperty]
        private bool videoExportCanStart;

        /// <summary>List of available export formats (e.g., MP4/AVI) for the UI dropdown.</summary>
        public ObservableCollection<VideoExportFormatOption> VideoExportFormatOptions { get; } = [];

        /// <summary>All previously saved reconstructions available to the user.</summary>
        public ObservableCollection<ReconstructionInfo> AvailableReconstructions { get; } = [];
        /// <summary>Currently filtered view over <see cref="AvailableReconstructions"/>.</summary>
        public ObservableCollection<ReconstructionInfo> FilteredReconstructions { get; } = [];

        /// <summary>
        /// Names of trend histories to series of numeric values displayed in charts. Populated on demand.
        /// </summary>
        private readonly Dictionary<string, ObservableCollection<double>> _metricTrendHistories = new();
        private const int TrendCanvasUpdateInterval = 10;
        private bool _hasPendingTrendUpdate;

        /// <summary>Time‑series of residual values for the trend chart.</summary>
        public ObservableCollection<double> ResidualHistory => GetTrendHistory(MetricKeys.Residual);

        /// <summary>Raised when a full result (i.e., at cycle end) is available.</summary>
        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        
        /// <summary>Raised for each processed inverse step with the frame payload.</summary>
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        /// <summary>Logical grouping of metrics for the UI "cards" on the Reconstruction page.</summary>
        public ObservableCollection<ReconstructionMetricGroupViewModel> MetricGroups { get; } = [];

        private readonly Dictionary<string, ReconstructionMetricViewModel> _metricsByKey = new();
        private readonly object _metricUpdateLock = new();
        private CancellationTokenSource? _metricUpdateCts;
        private readonly object _statisticsUpdateLock = new();
        private CancellationTokenSource? _statisticsUpdateCts;
        private ReconstructionResult? _latestResult;
        private ReconstructionFrame? _latestFrame;

        // ------------------------ Simplified gradient metrics ------------------------
        // A single list of norm/angle pairs with minimal metadata. No locking.
        private readonly List<GradientMetricSample> _gradientSamples = new();
        private int _selectedGradientIndex = -1;
        
        /// <summary>Raised when the gradient history collection changed (append/clear).</summary>
        public event EventHandler? GradientHistoryChanged;
        
        /// <summary>Raised when the selected gradient entry changed.</summary>
        public event EventHandler<int>? GradientSelectionChanged;
        
        /// <summary>Raised by the UI to request opening the gradient inspection popup.</summary>
        public event EventHandler? GradientInspectionRequested;
        
        /// <summary>Snapshot of the previous gradient used for angle calculation across steps.</summary>
        private Dictionary<int, double>? _previousGradientSnapshot; // used only to calculate angle

        private IErrorMetric? _cachedErrorMetric;
        private ErrorMetric _cachedErrorMetricChoice;

        /// <summary>Raised when the selected trend metric history changed and charts need refresh.</summary>
        public event EventHandler? SelectedTrendMetricHistoryChanged;

        /// <summary>Key identifying the currently selected trend history (e.g., residual).</summary>
        [ObservableProperty]
        private string selectedTrendMetricKey = MetricKeys.Residual;

        private SKSizeI _videoExportDistributionSize;
        private SKSizeI _videoExportColorbarSize;
        private SKSizeI _videoExportResidualSize;
        private SKSizeI _videoExportFrameSize;
        private int _videoExportFrameCount;
        private object _errorMetricLock = new object();

        /// <summary>Updates estimated size/time when the selected export format changes.</summary>
        partial void OnSelectedVideoExportFormatChanged(VideoExportFormatOption? value)
        {
            UpdateVideoExportEstimates();
        }

        /// <summary>Keeps UI phase state in sync with the export flags.</summary>
        partial void OnVideoExportIsRunningChanged(bool value)
        {
            UpdateVideoExportPhase();
        }

        /// <summary>Keeps UI phase state in sync when the export completes.</summary>
        partial void OnVideoExportHasResultChanged(bool value)
        {
            UpdateVideoExportPhase();
        }

        /// <summary>
        /// Constructs the view model, wires events, loads global parameters, and initialises metric cards.
        /// </summary>
        public ReconstructionPageViewModel(IReconstructionService reconstructionService,
                                           IBlockFemReconstructionService blockReconstructionService,
                                           IReconstructionExportService exportService)
        {
            _reconstructionService = reconstructionService;
            _blockReconstructionService = blockReconstructionService;
            _exportService = exportService;

            _reconstructionService.VisualizeIterations = VisualizeIterations;
            _blockReconstructionService.VisualizeIterations = VisualizeIterations;

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

            VirtualElectrodeSettings.PropertyChanged += OnVirtualElectrodeSettingsChanged;

            UpdateMetric(MetricKeys.IterationCount, IterationCount.ToString(CultureInfo.InvariantCulture));
            UpdateMetric(MetricKeys.ElapsedTime, FormatElapsed(ElapsedTime));
            UpdateMetric(MetricKeys.Residual, FormatDouble(Residual));
            UpdateMetric(MetricKeys.Correlation, FormatDouble(Correlation));

            _reconstructionService.ReconstructionUpdated += OnServiceReconstructionUpdated;
            _reconstructionService.ReconstructionFrameUpdated += OnServiceFrameUpdated;
            _blockReconstructionService.ReconstructionUpdated += OnServiceReconstructionUpdated;
            _blockReconstructionService.ReconstructionFrameUpdated += OnServiceFrameUpdated;
            RefreshMethodPickerOptions();

            _lastAppliedInitialDistributionType = ReconstructionParameters.InitialDistributionType;
        }

        /// <summary>
        /// Prepares the system for a new reconstruction run. Ensures the chosen discretization and
        /// parameters are initialised in the persistence layer and resets iteration counters if needed.
        /// If block configuration is active, initialises the dedicated block service instead.
        /// </summary>
        public void PrepareForNewReconstruction()
        {
            // Ensure we have the latest mesh and parameters from the workspace
            UpdateMesh();

            // Ensure parameters are up to date, if changed a new reconstruction will be initiated
            UpdateReconstructionParameters();

            // If block configuration was used, then this paths runs only
            if (ShouldUseBlockConfiguration)
            {
                var femMesh = _discretization ?? throw new NullReferenceException("Mesh was null during reconstruction initialization, check calling code!");

                var blockSignature = CreateCurrentRunSignature(femMesh);
                bool isSameBlockRun = _lastRunSignature?.Equals(blockSignature) ?? false;

                var config = Workspace.GetCompleteReconstructionConfiguration();
                bool configurationChanged = _lastBlockConfiguration != config;
                bool discretizationChanged = _initializedDiscretization != femMesh;

                if (configurationChanged || discretizationChanged)
                    isSameBlockRun = false;

                if (!_blockInitialized || !isSameBlockRun)
                {
                    _blockReconstructionService.Initialize();
                    _blockInitialized = true;
                    _initializedDiscretization = femMesh;
                    IterationCount = 0;
                }

                if (!isSameBlockRun)
                    _resetMetricsOnStart = true;

                _lastRunSignature = blockSignature;
                _lastBlockConfiguration = config;
                return;
            }

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

        /// <summary>
        /// Starts tracking changes on the given parameter object so the view model can reflect UI updates.
        /// </summary>
        private void TrackReconstructionParameters(ReconstructionRuntimeContext parameters)
        {
            if (_trackedParameters != null)
                _trackedParameters.PropertyChanged -= OnTrackedParametersPropertyChanged;

            _trackedParameters = parameters;
            if (_trackedParameters != null)
                _trackedParameters.PropertyChanged += OnTrackedParametersPropertyChanged;
        }

        /// <summary>
        /// Reacts to relevant parameter changes (drive pattern, parallelisation flags) by updating bound state.
        /// </summary>
        private void OnTrackedParametersPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReconstructionRuntimeContext.DrivePattern))
                SyncDrivePatternSelection();
            else if (e.PropertyName == nameof(ReconstructionRuntimeContext.DifferentialEquationSolver)
                     || e.PropertyName == nameof(ReconstructionRuntimeContext.UseOmpParallelization)
                     || e.PropertyName == nameof(ReconstructionRuntimeContext.UseCudaAcceleration))
                UpdateParallelizationToggleState();
        }

        /// <summary>
        /// Synchronizes the drive-pattern toggle state based on current parameters.
        /// </summary>
        private void SyncDrivePatternSelection()
        {
            if (ReconstructionParameters == null)
                return;

            SetDrivePattern(ReconstructionParameters.DrivePattern);
        }

        /// <summary>
        /// Adjusts the parallelisation toggle label for the active solver and refreshes the bound property.
        /// </summary>
        private void UpdateParallelizationToggleState()
        {
            if (ReconstructionParameters == null)
                return;

            ParallelizationToggleLabel = ReconstructionParameters.DifferentialEquationSolver == DifferentialEquationSolver.LBM
                ? "Use CUDA Parallelization"
                : "Use OMP Parallelization";

            OnPropertyChanged(nameof(UseParallelizationToggle));
        }

        /// <summary>
        /// Synchronizes the method picker options with the current reconstruction mode.
        /// Displays a non‑editable "Mixed" option when multiple blocks are configured in a category.
        /// When a single block is present per category, aligns parameters to the configuration so the
        /// pickers reflect the canvas selections.
        /// </summary>
        public void RefreshMethodPickerOptions()
        {
            var parameters = ReconstructionParameters;
            if (parameters == null)
                return;

            var configuration = Workspace.GetCompleteReconstructionConfiguration();
            var workspaceFlag = Workspace.GetUseBlockConfiguration();
            if (UseBlockConfiguration != workspaceFlag)
                UseBlockConfiguration = workspaceFlag;
            if (UseBlockConfiguration && configuration == null)
                UseBlockConfiguration = false;
            bool mixedErrorMetric = ShouldUseBlockConfiguration && configuration != null && configuration.Blocks.Count(b => b.Type == BlockType.ErrorMetric) > 1;
            bool mixedRegularizer = ShouldUseBlockConfiguration && configuration != null && configuration.Blocks.Count(b => b.Type == BlockType.Regularizer) > 1;
            bool mixedOptimizer = ShouldUseBlockConfiguration && configuration != null && configuration.Blocks.Count(b => b.Type == BlockType.Optimizer) > 1;

            // Align single selections to the configuration (multiple => read‑only "Mixed").
            if (ShouldUseBlockConfiguration && configuration != null)
            {
                if (!mixedErrorMetric && TryParseBlockEnum(configuration, BlockType.ErrorMetric, "metric_type", out ErrorMetric parsedError))
                    parameters.ErrorMetric = parsedError;

                if (!mixedRegularizer && TryParseBlockEnum(configuration, BlockType.Regularizer, "reg_tech", out RegularizationTechnique parsedReg))
                    parameters.RegularizationTechnique = parsedReg;

                if (!mixedOptimizer && TryParseBlockEnum(configuration, BlockType.Optimizer, "opt_algo", out NumericOptimizer parsedOpt))
                    parameters.NumericOptimizer = parsedOpt;
            }

            UpdateMethodPicker(ErrorMetricPickerOptions,
                               ErrorMetricOptions.Select(option => option.ToString()),
                               mixedErrorMetric,
                               parameters.ErrorMetric.ToString(),
                               value => SelectedErrorMetricDisplay = value,
                               enabled => IsErrorMetricPickerEnabled = enabled);

            UpdateMethodPicker(RegularizationPickerOptions,
                               RegularizationTechniqueOptions.Select(option => option.ToString()),
                               mixedRegularizer,
                               parameters.RegularizationTechnique.ToString(),
                               value => SelectedRegularizationDisplay = value,
                               enabled => IsRegularizationPickerEnabled = enabled);

            UpdateMethodPicker(OptimizerPickerOptions,
                               NumericOptimizerOptions.Select(option => option.ToString()),
                               mixedOptimizer,
                               parameters.NumericOptimizer.ToString(),
                               value => SelectedOptimizerDisplay = value,
                               enabled => IsOptimizerPickerEnabled = enabled);
        }

        /// <summary>
        /// Populates a method picker with either the available options or a locked "Mixed" placeholder.
        /// </summary>
        private void UpdateMethodPicker(ObservableCollection<string> target,
                                        IEnumerable<string> options,
                                        bool isMixed,
                                        string currentValue,
                                        Action<string?> setSelected,
                                        Action<bool> setEnabled)
        {
            target.Clear();

            if (isMixed)
            {
                target.Add(MixedPickerLabel);
                setSelected(MixedPickerLabel);
                setEnabled(false);
                return;
            }

            foreach (var option in options)
                target.Add(option);

            setSelected(target.FirstOrDefault(o => o == currentValue) ?? target.FirstOrDefault());
            setEnabled(true);
        }

        /// <summary>
        /// Attempts to synchronise an enum parameter with the value encoded in a block parameter.
        /// Case‑ and punctuation‑insensitive matching is used to be robust to UI strings.
        /// </summary>
        private static bool TryParseBlockEnum<T>(CompleteReconstructionConfiguration configuration,
                                                 BlockType blockType,
                                                 string parameterKey,
                                                 out T parsed)
            where T : struct, Enum
        {
            parsed = default;
            var block = configuration.Blocks.FirstOrDefault(b => b.Type == blockType);
            var rawValue = block?.Parameters.FirstOrDefault(p => p.Key == parameterKey).Value;
            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            static string Normalize(string input) => new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

            var normalized = Normalize(rawValue);
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (Normalize(name) == normalized)
                {
                    parsed = Enum.Parse<T>(name);
                    return true;
                }
            }

            return Enum.TryParse(rawValue, true, out parsed);
        }

        /// <summary>
        /// Updates UI radio state and propagates the selected drive pattern back to the parameters.
        /// </summary>
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
        #region Metrics and gradient history
        /// <summary>
        /// Creates the logical grouping of metric cards shown in the UI and registers trend‑selectable metrics.
        /// </summary>
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

            RegisterMetric("Gradient & Field Diagnostics", MetricKeys.GradientNorm, "Gradient L2 Norm");
            RegisterMetric("Gradient & Field Diagnostics", MetricKeys.GradientAngleChange, "Gradient Angle Δ");
            RegisterMetric("Gradient & Field Diagnostics", MetricKeys.PotentialRange, "Potential Range");
            RegisterMetric("Gradient & Field Diagnostics", MetricKeys.AdjointRange, "Adjoint Range");

            UpdateTrendSelectionStates();
        }

        /// <summary>
        /// Registers a metric card and ensures its trend series (if any) is available.
        /// </summary>
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

        /// <summary>
        /// Updates a single metric card text value by key.
        /// </summary>
        private void UpdateMetric(string key, string value)
        {
            if (_metricsByKey.TryGetValue(key, out var metric))
                metric.Value = value;
        }

        /// <summary>Retrieves a metric ViewModel by key if present.</summary>
        public ReconstructionMetricViewModel? GetMetricByKey(string key)
            => _metricsByKey.TryGetValue(key, out var metric) ? metric : null;

        /// <summary>Returns an immutable snapshot of a trend history by key.</summary>
        public IReadOnlyList<double> GetTrendHistorySnapshot(string key)
        {
            var history = GetTrendHistory(key);
            return history.ToArray();
        }

        /// <summary>Returns an immutable snapshot of the currently selected trend history.</summary>
        public IReadOnlyList<double> GetSelectedTrendHistorySnapshot()
            => GetTrendHistorySnapshot(SelectedTrendMetricKey);

        // ------------------------ Simplified gradient API (backed by _gradientSamples) ------------------------
        /// <summary>
        /// Returns a snapshot of gradient history for UI charts. Stores only norm and angle to stay light‑weight.
        /// </summary>
        public IReadOnlyList<GradientHistorySample> GetGradientHistorySnapshot()
        {
            // Convert lightweight samples to the existing view DTO to preserve UI compatibility.
            var list = new List<GradientHistorySample>(_gradientSamples.Count);
            foreach (var s in _gradientSamples)
            {
                list.Add(new GradientHistorySample(s.Iteration,
                                                    Array.Empty<double>(),
                                                    s.Norm,
                                                    s.FrameIndex,
                                                    s.Angle,
                                                    s.Iteration,
                                                    1));
            }
            return list;
        }

        /// <summary>Returns the gradient record at the given index, or null if out of range.</summary>
        public GradientHistorySample? GetGradientSample(int index)
        {
            if (index < 0 || index >= _gradientSamples.Count)
                return null;

            var s = _gradientSamples[index];
            return new GradientHistorySample(s.Iteration,
                                             Array.Empty<double>(),
                                             s.Norm,
                                             s.FrameIndex,
                                             s.Angle,
                                             s.Iteration,
                                             1);
        }

        /// <summary>Index of the currently selected gradient record.</summary>
        public int SelectedGradientIndex
        {
            get => _selectedGradientIndex;
        }

        /// <summary>Total number of gradient samples stored.</summary>
        public int GradientHistoryCount => _gradientSamples.Count;

        /// <summary>Updates the selected gradient index and raises the corresponding event.</summary>
        public void SetSelectedGradientIndex(int index)
        {
            if (index < -1)
                return;
            if (index >= _gradientSamples.Count)
                return;
            if (_selectedGradientIndex == index)
                return;

            _selectedGradientIndex = index;
            GradientSelectionChanged?.Invoke(this, index);
        }

        /// <summary>
        /// Moves the selection to the last gradient sample whose frame index is not greater than the provided one.
        /// Useful when the user scrubs through frames.
        /// </summary>
        public void SnapGradientSelectionToFrame(int frameIndex)
        {
            if (frameIndex < 0)
                frameIndex = 0;

            if (_gradientSamples.Count == 0)
                return;

            int target = -1;
            for (int i = 0; i < _gradientSamples.Count; i++)
            {
                if (_gradientSamples[i].FrameIndex <= frameIndex)
                    target = i;
                else
                    break;
            }

            if (target < 0)
                target = 0;

            if (target == _selectedGradientIndex)
                return;

            _selectedGradientIndex = target;
            GradientSelectionChanged?.Invoke(this, target);
        }

        /// <summary>Clears the gradient history and resets selection to "none".</summary>
        private void ClearGradientHistory()
        {
            bool hadHistory = _gradientSamples.Count > 0;
            int previousIndex = _selectedGradientIndex;

            _gradientSamples.Clear();
            _previousGradientSnapshot = null;
            _selectedGradientIndex = -1;

            if (hadHistory || previousIndex != -1)
            {
                GradientHistoryChanged?.Invoke(this, EventArgs.Empty);
                GradientSelectionChanged?.Invoke(this, -1);
            }
        }

        /// <summary>
        /// Returns a cached error metric instance for the given choice to avoid repeated factory allocations.
        /// </summary>
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

        /// <summary>Resets dynamic metric values and clears trend series.</summary>
        public void ResetReconstructionMetrics()
        {
            ResetMetricsCore();
            _resetMetricsOnStart = false;
        }

        /// <summary>
        /// Marks the start of a reconstruction step or loop. Starts the stopwatch and enables the UI timer.
        /// Clears trend histories on first use after Prepare.
        /// </summary>
        public void BeginReconstructionMetrics()
        {
            if (_resetMetricsOnStart)
            {
                ResetMetricsCore();
                _resetMetricsOnStart = false;
            }

            if (!_reconstructionStopwatch.IsRunning)
                _reconstructionStopwatch.Start();

            IsReconstructionRunning = true;
            StartElapsedTimer();
        }

        /// <summary>Stops the stopwatch and pauses UI updates without clearing history.</summary>
        public void PauseReconstructionMetrics()
        {
            if (_reconstructionStopwatch.IsRunning)
            {
                _reconstructionStopwatch.Stop();
                ElapsedTime = _reconstructionStopwatch.Elapsed;
            }

            FlushPendingTrendUpdates();
            StopElapsedTimer();
            IsReconstructionRunning = false;
        }

        /// <summary>Clears histories, resets counters and sets the page to idle state.</summary>
        public void StopReconstructionMetrics()
        {
            ResetMetricsCore();
            _resetMetricsOnStart = true;
            IsReconstructionRunning = false;
        }

        /// <summary>Core reset routine used by Begin/Stop paths; clears histories and cancels inflight metric tasks.</summary>
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

            _previousGradientSnapshot = null;

            ResetDynamicMetrics();
        }

        /// <summary>Sets all metric card values to placeholder and initialises some key ones.</summary>
        private void ResetDynamicMetrics()
        {
            foreach (var metric in _metricsByKey.Values)
                metric.Value = "—";

            UpdateMetric(MetricKeys.IterationCount, IterationCount.ToString(CultureInfo.InvariantCulture));
            UpdateMetric(MetricKeys.ElapsedTime, FormatElapsed(TimeSpan.Zero));
        }
        #endregion
       
        /// <summary>
        /// Central property change handler for this view model: reacts to parameter object replacement and
        /// re-wires listeners/pickers accordingly.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReconstructionParameters))
            {
                TrackReconstructionParameters(ReconstructionParameters);
                SyncDrivePatternSelection();
                UpdateParallelizationToggleState();
                RefreshMethodPickerOptions();
            }
        }

        /// <summary>Updates elapsed time derived metrics on each tick/assignment.</summary>
        partial void OnElapsedTimeChanged(TimeSpan value)
        {
            UpdateMetric(MetricKeys.ElapsedTime, FormatElapsed(value));
            UpdateIterationsPerSecond();
            UpdateTimePerIteration();
        }

        /// <summary>Recomputes iteration‑dependent UX metrics when the iteration count changes.</summary>
        partial void OnIterationCountChanged(int value)
        {
            UpdateMetric(MetricKeys.IterationCount, value.ToString(CultureInfo.InvariantCulture));
            UpdateIterationsPerSecond();
            UpdateTimePerIteration();
            OnPropertyChanged(nameof(CanEditInitialDistribution));
        }

        /// <summary>Propagates the latest residual value to the metric card.</summary>
        partial void OnResidualChanged(double value)
            => UpdateMetric(MetricKeys.Residual, FormatDouble(value));

        /// <summary>Propagates the latest correlation value to the metric card.</summary>
        partial void OnCorrelationChanged(double value)
            => UpdateMetric(MetricKeys.Correlation, FormatDouble(value));

        /// <summary>Computes iterations/second when enough data is available; otherwise shows placeholder.</summary>
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

        /// <summary>Computes the average time per iteration from elapsed time and iteration count.</summary>
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

        /// <summary>Updates the residual drop/iteration metric once at least two samples exist.</summary>
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

        /// <summary>Starts the UI timer if not already running.</summary>
        private void StartElapsedTimer()
        {
            if (!_elapsedTimer.Enabled)
                _elapsedTimer.Start();
        }

        /// <summary>Stops the UI timer if running.</summary>
        private void StopElapsedTimer()
        {
            if (_elapsedTimer.Enabled)
                _elapsedTimer.Stop();
        }

        partial void OnReconstructionSearchTextChanged(string value) => ApplyReconstructionFilter();

        /// <summary>Refreshes the cached discretization reference from the workspace and source selection.</summary>
        private void UpdateMesh()
        {
            _discretization = Workspace.GetDiscretization();
            RefreshMeasurementSourceSelection();
        }

        /// <summary>
        /// Applies the configured initial conductivity distribution to the active discretization
        /// and updates workspace snapshots so canvases render the expected state when the page opens
        /// or when the initial distribution method changes.
        /// </summary>
        /// <param name="force">When true, reapply even if a matching initial distribution already exists.</param>
        public void SyncInitialDistribution(bool force = false)
        {
            var mesh = Workspace.GetDiscretization();
            var parameters = ReconstructionParameters;

            if (mesh == null || parameters == null)
                return;

            if (!force &&
                Workspace.GetInitialConductivityDistribution() != null &&
                _lastAppliedInitialDistributionType == parameters.InitialDistributionType)
            {
                return;
            }

            var originalSnapshot = Workspace.GetOriginalConductivityDistribution()
                                   ?? Workspace.GetOriginalDiscretization()?.GetConductivityDistribution();
            if (originalSnapshot != null)
            {
                Workspace.SetOriginalConductivityDistribution(new ConductivityDistribution(originalSnapshot.Conductivities));
            }

            var initial = ConductivityDistributionFactory.CreateInitialDistribution(mesh, parameters.InitialDistributionType);
            var initialCopy = new ConductivityDistribution(initial.Conductivities);

            mesh.SetConductivityDistribution(new ConductivityDistribution(initialCopy.Conductivities));
            Workspace.SetInitialConductivityDistribution(initialCopy);
            Workspace.SetInitialDiscretization(mesh.DeepCopy());

            _lastAppliedInitialDistributionType = parameters.InitialDistributionType;
        }

        /// <summary>
        /// Marks the current initial distribution selection as applied so the view model
        /// does not regenerate it unnecessarily on the next sync.
        /// </summary>
        public void AcknowledgeInitialDistributionUpdate()
        {
            var parameters = ReconstructionParameters;
            if (parameters != null)
            {
                _lastAppliedInitialDistributionType = parameters.InitialDistributionType;
            }
        }

        /// <summary>Refreshes the current reconstruction parameters from the workspace and pickers.</summary>
        private void UpdateReconstructionParameters()
        {
            ReconstructionParameters = Workspace.GetReconstructionParameters();
            RefreshMethodPickerOptions();
        }

        /// <summary>
        /// Ensures the persistence layer is initialised for the current discretization/parameters.
        /// The block pipeline is initialised on demand when active.
        /// </summary>
        private void InitializeReconstruction(bool force = false)
        {
            UpdateMesh();
            UpdateReconstructionParameters();

            if (ShouldUseBlockConfiguration)
            {
                var config = Workspace.GetCompleteReconstructionConfiguration();
                if (_lastBlockConfiguration != config)
                {
                    _blockInitialized = false;
                    _lastBlockConfiguration = config;
                }

                if (force || !_blockInitialized)
                {
                    _blockReconstructionService.Initialize();
                    _blockInitialized = true;
                    IterationCount = 0;
                    _initializedDiscretization = Workspace.GetDiscretization();
                }

                return;
            }

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

        /// <summary>
        /// Verifies that the currently selected solver matches the active discretization type
        /// to prevent invalid combinations (e.g., LBM solver on a FEM mesh).
        /// </summary>
        public bool CheckReconstructionMethodAgainstDiscretization()
        {
            if(_discretization is FEMMesh)
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FEM)
                    return false;
            else if(_discretization is LBMGrid)
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.LBM)
                    return false;

            return true;
        }

        /// <summary>
        /// Debounces metric updates by cancelling in‑flight computations and starting a fresh task.
        /// This prevents redundant work when frames/results arrive in quick succession.
        /// </summary>
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

        /// <summary>
        /// Computes distribution, electrode, and field metrics for the last frame/result on a background thread.
        /// Results are marshalled to the UI thread before being written to bound properties.
        /// </summary>
        private async Task ComputeMetricsAsync(ReconstructionResult? result, ReconstructionFrame? frame, CancellationToken token)
        {
            try
            {
                DistributionMetrics? distributionMetrics = null;
                if (result != null)
                    distributionMetrics = ReconstructionStatistics.ComputeDistributionMetrics(result, token, true);
                
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

                    if (fieldMetrics.HasData)
                    {
                        UpdateMetric(MetricKeys.GradientNorm, FormatDouble(fieldMetrics.GradientNorm));
                        UpdateMetric(MetricKeys.GradientAngleChange,
                                     fieldMetrics.GradientAngle.HasValue
                                         ? $"{FormatDouble(fieldMetrics.GradientAngle.Value, "F1")}°"
                                         : "—");
                        UpdateMetric(MetricKeys.PotentialRange, FormatRange(fieldMetrics.PotentialRange));
                        UpdateMetric(MetricKeys.AdjointRange, FormatRange(fieldMetrics.AdjointRange));

                        // Record simplified gradient metric sample (norm + angle)
                        int frameIndex = Math.Max(Workspace.GetReconstructionFrames().Count - 1, 0);
                        _gradientSamples.Add(new GradientMetricSample(IterationCount,
                                                                      frameIndex,
                                                                      fieldMetrics.GradientNorm,
                                                                      fieldMetrics.GradientAngle ?? double.NaN));
                        _selectedGradientIndex = _gradientSamples.Count - 1;
                        GradientHistoryChanged?.Invoke(this, EventArgs.Empty);
                        GradientSelectionChanged?.Invoke(this, _selectedGradientIndex);

                        // Keep last snapshot for next angle computation
                        _previousGradientSnapshot = fieldMetrics.GradientSnapshot;
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

        /// <summary>
        /// Computes per‑electrode error statistics against the current original distribution (if available).
        /// </summary>
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

        /// <summary>
        /// Computes field‑level diagnostics (gradient norm/angle, potential/adjoint ranges) for a frame.
        /// </summary>
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
            var previous = _previousGradientSnapshot;

            if (previous != null && previous.Count > 0 && gradient.Count > 0)
            {
                gradientAngle = ReconstructionStatistics.ComputeGradientAngle(previous, gradient);
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

        /// <summary>
        /// Computes a numeric range (min,max) for the given dictionary of values, ignoring NaN/Inf.
        /// </summary>
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

        /// <summary>
        /// Computes the L2 energy of the provided regularization vector.
        /// </summary>
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

        /// <summary>Formats a number according to the default or provided format string.</summary>
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

        /// <summary>Formats a value as a percentage with one decimal, guarding for invalid numbers.</summary>
        private static string FormatPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "—";
            return value.ToString("P1", CultureInfo.InvariantCulture);
        }

        /// <summary>Formats a <see cref="TimeSpan"/> as mm:ss or hh:mm:ss depending on magnitude.</summary>
        private static string FormatElapsed(TimeSpan value)
            => value.TotalHours >= 1.0
                ? value.ToString("hh\\:mm\\:ss")
                : value.ToString("mm\\:ss");

        /// <summary>Formats a numeric range into a human‑readable string with delta (Δ).</summary>
        private static string FormatRange(RangeMetrics range)
        {
            if (!range.HasValue)
                return "—";

            if (range.Max is null || range.Min is null)
                return "—";

            double delta = range.Max.Value - range.Min.Value;
            return $"{range.Min.Value.ToString("F3", CultureInfo.InvariantCulture)} to {range.Max.Value.ToString("F3", CultureInfo.InvariantCulture)} (Δ {delta.ToString("F3", CultureInfo.InvariantCulture)})";
        }

        /// <summary>
        /// Converts raw trend values to display strings depending on the chosen metric.
        /// </summary>
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

        /// <summary>Well‑known keys used to access metric cards and trend histories.</summary>
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

        /// <summary>Composite of RMSE/MAE/MAPE on electrode signals and optional metric value.</summary>
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

        /// <summary>Container for frame‑level diagnostics used by the UI.</summary>
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

        /// <summary>Simple min/max pair with presence flag used by range formatting.</summary>
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

        /// <summary>Lightweight record used to display gradient history (norm &amp; angle only).</summary>
        private readonly record struct GradientMetricSample(int Iteration, int FrameIndex, double Norm, double Angle);

        /// <summary>
        /// Starts a background reconstruction run. Chooses the block or classic service based on current mode,
        /// kicks off metrics, and returns immediately. The service raises events as frames/results are produced.
        /// </summary>
        public void StartBackgroundReconstruction()
        {
            PrepareForNewReconstruction();
            BeginReconstructionMetrics();

            if (ShouldUseBlockConfiguration)
            {
                _ = _blockReconstructionService.RunFullReconstructionCycleAsync(StepSize,
                                                                                 RegularizationWeight,
                                                                                 ExcitationCurrentAmplitude);
                return;
            }

            _reconstructionService.StartBackgroundReconstruction(MaxIterationCount, StepSize, RegularizationWeight, ExcitationCurrentAmplitude);
        }

        /// <summary>Pauses the background run (if any) and freezes metrics updates.</summary>
        public void PauseReconstruction()
        {
            if (ShouldUseBlockConfiguration)
            {
                PauseReconstructionMetrics();
                return;
            }

            _reconstructionService.PauseBackgroundReconstruction();
            PauseReconstructionMetrics();
        }

        /// <summary>Resumes the background run (if any) and re‑enables metrics updates.</summary>
        public void ResumeReconstruction()
        {
            if (ShouldUseBlockConfiguration)
            {
                BeginReconstructionMetrics();
                return;
            }

            _reconstructionService.ResumeBackgroundReconstruction();
            BeginReconstructionMetrics();
        }

        /// <summary>
        /// Stops the background run (if any) and resets the view model state to idle.
        /// Clears block initialisation flags so a future run starts cleanly.
        /// </summary>
        public void StopReconstruction()
        {
            if (ShouldUseBlockConfiguration)
            {
                StopReconstructionMetrics();
                _blockInitialized = false;
                _initializedDiscretization = null;
                _lastRunSignature = null;
                return;
            }

            _reconstructionService.StopBackgroundReconstruction();
            StopReconstructionMetrics();
            _initializedDiscretization = null;
            _lastRunSignature = null;
        }

        /// <summary>
        /// Clears frames/results and view model state, returning the page to a fresh state.
        /// Does not modify global workspace parameters.
        /// </summary>
        public void RestartReconstruction()
        {
            StopReconstruction();
            // Reset view model state and clear workspace frames/results
            Workspace.ClearReconstructionFrames();
            Workspace.SetReconstructionResults(new List<ReconstructionResult>());

            _blockInitialized = false;

            IterationCount = 0;
            Residual = 1.0;
            Correlation = 0.0;
            ElapsedTime = TimeSpan.Zero;
        }

        /// <summary>Restores UI and parameter defaults useful for quick experimentation.</summary>
        public void ResetAllToDefaults()
        {
            var defaults = new ReconstructionRuntimeContext();
            ReconstructionParameters = defaults;

            MaxIterationCount = 50;
            StepSize = 0.001;
            RegularizationWeight = 0.001;

            ExcitationElectrodeId = 1;
            GroundElectrodeId = 0;
            SetDrivePattern(DrivePattern.Adjecent);
        }

        /// <summary>
        /// Runs a full reconstruction cycle on the calling thread pool task and returns the aggregated result.
        /// </summary>
        public Task<ReconstructionResult?> RunFullReconstructionCycleAsync()
        {
            InitializeReconstruction();
            BeginReconstructionMetrics();

            if (ShouldUseBlockConfiguration)
            {
                var blockResult = _blockReconstructionService.RunFullReconstructionCycleAsync(StepSize,
                                                                                               RegularizationWeight,
                                                                                               ExcitationCurrentAmplitude);
                StopElapsedTimer();
                return blockResult;
            }

            var result = _reconstructionService.RunFullReconstructionCycleAsync(StepSize,
                                                                             RegularizationWeight,
                                                                             ExcitationCurrentAmplitude);
            StopElapsedTimer();

            return result;
        }

        /// <summary>
        /// Performs a single reconstruction step asynchronously and updates metrics/UI accordingly.
        /// </summary>
        public async Task StepReconstructionAsync()
        {
            InitializeReconstruction();
            BeginReconstructionMetrics();

            if (ShouldUseBlockConfiguration)
            {
                await _blockReconstructionService.StepReconstructionAsync(StepSize,
                                                                          RegularizationWeight,
                                                                          ExcitationCurrentAmplitude);
                StopElapsedTimer();
                FlushPendingTrendUpdates();
                return;
            }

            await _reconstructionService.StepReconstructionAsync();
            StopElapsedTimer();
            FlushPendingTrendUpdates();
        }

        // Event handlers wired to the service --------------------------------------------------------------
        /// <summary>Schedules a recomputation of statistics whenever a full result is emitted by a service.</summary>
        private void OnServiceReconstructionUpdated(object? sender, ReconstructionResult result)
        {
            ScheduleReconstructionStatisticsUpdate(result);
        }

        /// <summary>Notifies the view that a new frame is available and requests metric updates.</summary>
        private void OnServiceFrameUpdated(object? sender, ReconstructionFrame frame)
        {
            RequestMetricUpdate(null, frame);
            ReconstructionFrameUpdated?.Invoke(this, frame);
        }

        /// <summary>Saves all reconstruction results under the current friendly name using the service layer.</summary>
        public void SaveReconstruction()
        {
            var results = Workspace.GetReconstructionResults();
            if (results.Count == 0 || string.IsNullOrWhiteSpace(Name))
                return;
            _reconstructionService.SaveReconstruction(results, Name, ReconstructionParameters);
            LoadAvailableReconstructions();
        }

        /// <summary>Loads a reconstruction from a file path and mirrors it to the workspace for UI access.</summary>
        public void LoadReconstruction(string filePath)
        {
            _reconstructionService.LoadReconstruction(filePath);
        }

        /// <summary>Refreshes the list of saved reconstructions (metadata only) and applies the current filter.</summary>
        public void LoadAvailableReconstructions()
        {
            AvailableReconstructions.Clear();
            foreach (var r in _reconstructionService.GetReconstructions())
                AvailableReconstructions.Add(r);
            ApplyReconstructionFilter();
        }

        /// <summary>
        /// Prepares UI state for the video export popup and computes preview dimensions/estimates.
        /// </summary>
        public void PrepareVideoExportOptions(SKSize distributionCanvasSize,
                                              SKSize colorbarCanvasSize,
                                              SKSize residualCanvasSize,
                                              PotentialDisplayMode mode)
        {
            ResetVideoExportState();

            _selectedPotentialDisplayMode = mode;

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

        /// <summary>
        /// Generates a reconstruction video by rendering per‑frame images and encoding them into the
        /// requested container. Returns a rich result with success flag and file path or error details.
        /// </summary>
        public Task<VideoExportResult> ExportReconstructionVideoAsync(SKSize distributionCanvasSize,
                                                                       SKSize colorbarCanvasSize,
                                                                       SKSize residualCanvasSize,
                                                                       PotentialDisplayMode mode,
                                                                       VideoExportContainer container,
                                                                       IProgress<VideoExportProgressReport>? progress = null,
                                                                       CancellationToken cancellationToken = default)
        {
            _selectedPotentialDisplayMode = mode;
            throw new NotImplementedException();
            //return ReconstructionVideoExportWorkflow.ExportAsync(distributionCanvasSize,
            //                                                     colorbarCanvasSize,
            //                                                     residualCanvasSize,
            //                                                     _selectedPotentialDisplayMode,
            //                                                     container,
            //                                                     progress,
            //                                                     cancellationToken);
        }

        /// <summary>
        /// Exports reconstruction data (frames, metrics and ancillary information) to the app data directory
        /// using the configured export service.
        /// </summary>
        public Task<DataExportResult> ExportReconstructionDataAsync(PotentialDisplayMode potentialMode,
                                                                     ConductivityDisplayMode conductivityMode)
        {
            string rootDirectory = FileSystem.Current.AppDataDirectory;
            _selectedPotentialDisplayMode = potentialMode;
            _selectedConductivityDisplayMode = conductivityMode;
            return _exportService.ExportAsync(rootDirectory, _selectedPotentialDisplayMode, _selectedConductivityDisplayMode);
        }

        /// <summary>
        /// Computes distribution metrics for the initial state only (used by certain visualisations).
        /// </summary>
        private static DistributionMetrics? ComputeInitialDistributionMetrics(ReconstructionResult snapshot)
        {
            var initialResult = new ReconstructionResult(snapshot.OriginalConductivityDistribution,
                                                         snapshot.InitialConductivitiyDistribution,
                                                         snapshot.InitialConductivitiyDistribution,
                                                         snapshot.Frames);
            return ReconstructionStatistics.ComputeDistributionMetrics(initialResult, CancellationToken.None, true);
        }

        /// <summary>Returns the last available frame for a result or falls back to the provided frame.</summary>
        private static ReconstructionFrame GetFrameForResult(ReconstructionResult result, ReconstructionFrame fallback)
            => result.Frames.LastOrDefault() ?? fallback;

        /// <summary>Switches the export popup to the running state and resets progress.</summary>
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

        /// <summary>Updates the progress bar and status text during export.</summary>
        public void UpdateVideoExportProgress(VideoExportProgressReport report)
        {
            UpdateVideoExportProgressCore(report.Progress);
            VideoExportStatusMessage = report.StatusMessage;
        }

        /// <summary>Sets a status message indicating that the export cancellation was requested.</summary>
        public void NotifyVideoExportAborting()
        {
            VideoExportStatusMessage = "Aborting video generation...";
        }

        /// <summary>Finalises the export popup state and shows success or error information.</summary>
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

        /// <summary>Resets all UI state for the video export popup back to defaults.</summary>
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

        /// <summary>Sets progress properties with bounds checking and formatted percentage text.</summary>
        private void UpdateVideoExportProgressCore(double progress)
        {
            double clamped = Math.Clamp(progress, 0.0, 1.0);
            VideoExportProgress = clamped;
            VideoExportProgressPercentText = $"{Math.Round(clamped * 100.0)}%";
        }

        /// <summary>Updates best‑effort file size/time estimates for the current export settings.</summary>
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

        /// <summary>Updates UI booleans that drive the export popup's visual states.</summary>
        private void UpdateVideoExportPhase()
        {
            VideoExportIsConfiguring = !VideoExportIsRunning && !VideoExportHasResult;
            UpdateVideoExportCanStart();
        }

        /// <summary>Evaluates whether the export job can be started given the current inputs.</summary>
        private void UpdateVideoExportCanStart()
        {
            bool canStart = !VideoExportIsRunning
                            && !VideoExportHasResult
                            && SelectedVideoExportFormat is not null
                            && _videoExportFrameCount > 0;

            VideoExportCanStart = canStart;
        }

        /// <summary>Applies the name filter to the available reconstructions list.</summary>
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

        /// <summary>Creates a signature object that encodes relevant parameters for run equality checks.</summary>
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
                parameters.DrivePatternSkip,
                parameters.UsePotentialDifferences,
                parameters.UseOmpParallelization,
                parameters.UseCudaAcceleration);

            return new ReconstructionRunSignature(mesh, snapshot);
        }

        /// <summary>Retrieves or creates the trend history collection for the given key.</summary>
        private ObservableCollection<double> GetTrendHistory(string key)
        {
            if (!_metricTrendHistories.TryGetValue(key, out var history))
            {
                history = new ObservableCollection<double>();
                _metricTrendHistories[key] = history;
            }

            return history;
        }

        /// <summary>Adds a single sample to the specified trend history and notifies the chart layer.</summary>
        private void AddTrendSample(string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            var history = GetTrendHistory(key);
            history.Add(value);
            NotifyTrendHistoryChanged(key);
        }

        /// <summary>Queues a chart refresh when the selected series changes or enough samples accumulate.</summary>
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

        /// <summary>
        /// Processes expensive statistics (residual, correlation) and updates the trend series on the UI thread.
        /// Cancels superseded computations to prevent backlogs.
        /// </summary>
        private async Task ProcessReconstructionStatisticsAsync(ReconstructionResult result, CancellationTokenSource cts)
        {
            try
            {
                var token = cts.Token;
                double residual = ReconstructionStatistics.CalculateResidual(result, true);
                token.ThrowIfCancellationRequested();

                double correlation = ReconstructionStatistics.CalculateCorrelation(result.ReconstructedConductivityDistribution,
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

        /// <summary>
        /// Triggers a chart refresh if the changed series is the one currently plotted. Throttles refresh
        /// to every N samples during a run to reduce UI overhead.
        /// </summary>
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

        /// <summary>Raises the chart refresh event on the UI thread.</summary>
        private void RaiseSelectedTrendMetricHistoryChanged()
        {
            MainThread.BeginInvokeOnMainThread(() => SelectedTrendMetricHistoryChanged?.Invoke(this, EventArgs.Empty));
        }

        /// <summary>Executes any pending deferred chart refresh at the end of a step/run.</summary>
        private void FlushPendingTrendUpdates()
        {
            if (!_hasPendingTrendUpdate)
                return;

            _hasPendingTrendUpdate = false;
            RaiseSelectedTrendMetricHistoryChanged();
        }

        /// <summary>Marks the selected metric as active so only one trend is plotted at a time.</summary>
        private void UpdateTrendSelectionStates()
        {
            foreach (var metric in _metricsByKey.Values)
                metric.IsTrendSelected = metric.Key == SelectedTrendMetricKey;
        }

        /// <summary>Updates trend selection and triggers an immediate chart refresh.</summary>
        partial void OnSelectedTrendMetricKeyChanged(string value)
        {
            UpdateTrendSelectionStates();
            _hasPendingTrendUpdate = false;
            RaiseSelectedTrendMetricHistoryChanged();
        }

        /// <summary>Command handler to select a trend metric from the UI.</summary>
        [RelayCommand]
        private void SelectTrendMetric(string metricKey)
        {
            if (!_metricsByKey.TryGetValue(metricKey, out var metric) || !metric.IsTrendSelectable)
                return;

            SelectedTrendMetricKey = metricKey;
        }

        /// <summary>Command handler that requests opening the gradient inspection popup.</summary>
        [RelayCommand]
        private void RequestGradientInspection(string metricKey)
        {
            if (string.IsNullOrWhiteSpace(metricKey))
                return;

            // Do not open the gradient popup while reconstruction is running to avoid UI slowdowns.
            if (IsReconstructionRunning)
                return;

            if (metricKey == MetricKeys.GradientNorm
                || metricKey == MetricKeys.GradientAngleChange
                || metricKey == MetricKeys.PotentialRange
                || metricKey == MetricKeys.AdjointRange)
            {
                GradientInspectionRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Holds the actual value displayed in the gradient history flyout. It stores the displayed value,
        /// whether the sample is an aggregation, and exposes immutable vector accessors.
        /// </summary>
        public sealed class GradientHistorySample
        {
            private readonly double[] _vector;

            public GradientHistorySample(int iteration,
                                         double[] vector,
                                         double norm,
                                         int frameIndex,
                                         double? angle)
                : this(iteration, vector, norm, frameIndex, angle, iteration, 1)
            {
            }

            public GradientHistorySample(int iteration,
                                         double[] vector,
                                         double norm,
                                         int frameIndex,
                                         double? angle,
                                         int firstIteration,
                                         int collapsedCount)
            {
                Iteration = iteration;
                FirstIteration = Math.Min(firstIteration, iteration);
                CollapsedCount = Math.Max(1, collapsedCount);
                _vector = vector;
                Norm = norm;
                FrameIndex = frameIndex;
                Angle = angle;
            }

            /// <summary>Iteration index associated with this sample.</summary>
            public int Iteration { get; }
            /// <summary>First iteration covered by this sample when it represents an aggregation.</summary>
            public int FirstIteration { get; }
            /// <summary>Number of original samples collapsed into this sample.</summary>
            public int CollapsedCount { get; }
            /// <summary>L2 norm of the gradient vector.</summary>
            public double Norm { get; }
            /// <summary>Index of the frame used to compute the metric.</summary>
            public int FrameIndex { get; }
            /// <summary>Change in gradient direction compared to the previous sample, in degrees.</summary>
            public double? Angle { get; }
            /// <summary>True when multiple original entries were collapsed into this sample.</summary>
            public bool IsAggregated => CollapsedCount > 1;
            /// <summary>Read‑only view of the gradient vector used by the inspector UI.</summary>
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
            int DrivePatternSkip,
            bool UsePotentialDifferences,
            bool UseOmpParallelization,
            bool UseCudaAcceleration);

        private record ReconstructionRunSignature(IDiscretization Mesh, ReconstructionParametersSnapshot Parameters);
    }

    /// <summary>Logical group of metric tiles shown on the Reconstruction page.</summary>
    public sealed partial class ReconstructionMetricGroupViewModel : ObservableObject
    {
        /// <summary>Creates a new metric group with the given display title.</summary>
        public ReconstructionMetricGroupViewModel(string title)
        {
            Title = title;
        }

        /// <summary>Display title for the group.</summary>
        public string Title { get; }

        /// <summary>List of metric tiles in this group.</summary>
        public ObservableCollection<ReconstructionMetricViewModel> Metrics { get; } = [];
    }

    /// <summary>View model for a single metric tile, optionally trend‑selectable.</summary>
    public sealed partial class ReconstructionMetricViewModel : ObservableObject
    {
        /// <summary>Constructs a metric tile with identity and trend category.</summary>
        public ReconstructionMetricViewModel(string key, string name, TrendMetricCategory trendCategory)
        {
            Key = key;
            Name = name;
            TrendCategory = trendCategory;
            IsTrendSelectable = trendCategory != TrendMetricCategory.None;
        }

        /// <summary>Unique key used for lookups and trend series selection.</summary>
        public string Key { get; }
        /// <summary>Display name shown to the user.</summary>
        public string Name { get; }
        /// <summary>Category used to build the trend picker menu.</summary>
        public TrendMetricCategory TrendCategory { get; }
        /// <summary>True when this metric can be plotted over time.</summary>
        public bool IsTrendSelectable { get; }

        /// <summary>Formatted text value shown on the tile.</summary>
        [ObservableProperty]
        private string value = "—";

        /// <summary>Marks whether the tile is currently the selected trend target.</summary>
        [ObservableProperty]
        private bool isTrendSelected;
    }

    /// <summary>Categories for grouping trend metrics in the UI.</summary>
    public enum TrendMetricCategory
    {
        None,
        Residual,
        ErrorNorm,
        Similarity
    }
}
