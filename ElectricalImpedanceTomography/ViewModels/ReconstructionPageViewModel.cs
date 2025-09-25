using CommunityToolkit.Mvvm.ComponentModel;
using ElectricalImpedanceTomography.Helpers;
using Microsoft.Maui.Storage;
using ServiceLayer;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Factories;
using Utility.Classes.ReconstructionParameters;

using Workspace = Utility.Classes.Application.Workspace;
using Timer = System.Timers.Timer;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        private readonly Stopwatch _reconstructionStopwatch = new();
        private readonly Timer _elapsedTimer;

        private IDiscretization? _discretization = Workspace.GetDiscretization();
        private IDiscretization? _initializedDiscretization;
        private ReconstructionRunSignature? _lastRunSignature;
        private bool _resetMetricsOnStart = true;

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

        [ObservableProperty]
        private bool videoExportIsRunning;

        [ObservableProperty]
        private bool videoExportHasResult;

        [ObservableProperty]
        private bool videoExportWasSuccessful;

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

            StopElapsedTimer();
        }

        public void StopReconstructionMetrics()
        {
            ResetMetricsCore();
            _resetMetricsOnStart = true;
        }

        private void ResetMetricsCore()
        {
            ResidualHistory.Clear();
            Residual = 0.0;
            Correlation = 0.0;
            ElapsedTime = TimeSpan.Zero;
            _reconstructionStopwatch.Reset();
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
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FEM)
                    return false;
            else if(_discretization is LBMGrid)
                if (ReconstructionParameters.DifferentialEquationSolver != Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.LBM)
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
            var result = _reconstructionService.RunFullReconstructionCycleAsync(StepSize,
                                                                         RegularizationWeight,
                                                                         ExcitationCurrentAmplitude);
            StopElapsedTimer();

            return result;
        }

        private void OnServiceReconstructionUpdated(object? sender, ReconstructionResult result)
        {
            Residual = CalculateResidual(result.ReconstructedConductivityDistribution,
                                         result.OriginalConductivityDistribution);
            Correlation = CalculateCorrelation(result.ReconstructedConductivityDistribution,
                                               result.OriginalConductivityDistribution);
            ElapsedTime = _reconstructionStopwatch.Elapsed;
            IterationCount++;
            ResidualHistory.Add(Residual);

            if(IterationCount % 10 == 0)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ReconstructionUpdated?.Invoke(this, result);
                });
            }
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

        public async Task<VideoExportResult> ExportReconstructionVideoAsync(SKSize distributionCanvasSize,
                                                                            SKSize colorbarCanvasSize,
                                                                            SKSize residualCanvasSize,
                                                                            PotentialDisplayMode mode,
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
                .Select(r => CalculateResidual(r.ReconstructedConductivityDistribution,
                                               r.OriginalConductivityDistribution))
                .ToList();

            var distributionSize = ReconstructionVideoRenderer.NormalizeSize(distributionCanvasSize, 250, 250);
            var colorbarSize = ReconstructionVideoRenderer.NormalizeSize(colorbarCanvasSize, 250, 20);
            var residualSize = ReconstructionVideoRenderer.NormalizeSize(residualCanvasSize, 600, 170);

            string directory = FileSystem.Current.AppDataDirectory;
            Directory.CreateDirectory(directory);
            string filePath = Path.Combine(directory, $"reconstruction_{DateTime.Now:yyyyMMdd_HHmmss}.avi");

            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    AviVideoWriter.AviVideoStream? videoStream = null;
                    int videoWidth = 0;
                    int videoHeight = 0;

                    using var stream = File.Create(filePath);
                    double totalSteps = frames.Count + 1.0;

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

                            if (videoStream == null)
                            {
                                videoStream = AviVideoWriter.BeginWrite(stream,
                                                                        videoWidth,
                                                                        videoHeight,
                                                                        Workspace.ReconstructionVideoFramesPerSecond,
                                                                        frameBytes.Length,
                                                                        AviVideoWriter.AviVideoCodec.MotionJpeg);
                            }

                            videoStream.WriteFrame(frameBytes);
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        progress?.Report(new VideoExportProgressReport(Math.Min(0.98, frames.Count / (frames.Count + 1.0)),
                                                                        "Finalizing video stream..."));

                        if (videoStream == null)
                            throw new InvalidOperationException("Unable to initialize the AVI writer.");

                        videoStream.Complete();
                    }
                    finally
                    {
                        videoStream?.Dispose();
                    }
                });

                progress?.Report(new VideoExportProgressReport(1.0, "Video generation completed."));
                return VideoExportResult.CreateSuccess(filePath);
            }
            catch (OperationCanceledException)
            {
                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch
                    {
                        // Ignore any errors encountered during cleanup.
                    }
                }

                return VideoExportResult.CreateFailure("Export Aborted", "The video export was aborted.");
            }
            catch (Exception ex)
            {
                return VideoExportResult.CreateFailure("Export Failed", ex.Message);
            }
        }

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
        }

        private void UpdateVideoExportProgressCore(double progress)
        {
            double clamped = Math.Clamp(progress, 0.0, 1.0);
            VideoExportProgress = clamped;
            VideoExportProgressPercentText = $"{Math.Round(clamped * 100.0)}%";
        }

        private void ApplyReconstructionFilter()
        {
            FilteredReconstructions.Clear();
            foreach (var r in AvailableReconstructions.Where(r =>
                         string.IsNullOrWhiteSpace(ReconstructionSearchText) ||
                         r.Name.Contains(ReconstructionSearchText, StringComparison.OrdinalIgnoreCase)))
                FilteredReconstructions.Add(r);
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
                //StepSize,
                RegularizationWeight,
                //MaxIterationCount,
                ExcitationCurrentAmplitude,
                ExcitationElectrodeId,
                GroundElectrodeId,
                AdjecentDrivePattern,
                OppositeDrivePattern);

            return new ReconstructionRunSignature(mesh, snapshot);
        }

        private readonly record struct ReconstructionParametersSnapshot(
            DifferentialEquationSolver DifferentialEquationSolver,
            RegularizationTechnique RegularizationTechnique,
            ErrorMetric ErrorMetric,
            NumericSolver NumericSolver,
            NumericOptimizer NumericOptimizer,
            InitialDistributionTypes InitialDistributionType,
            //double StepSize,
            double RegularizationWeight,
            //int MaxIterationCount,
            double ExcitationCurrentAmplitude,
            int ExcitationElectrodeId,
            int GroundElectrodeId,
            bool AdjecentDrivePattern,
            bool OppositeDrivePattern);

        private record ReconstructionRunSignature(IDiscretization Mesh, ReconstructionParametersSnapshot Parameters);
    }
}