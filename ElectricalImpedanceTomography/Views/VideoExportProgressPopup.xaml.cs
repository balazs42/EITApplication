using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.Helpers;
using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using System;
using System.Threading;

namespace ElectricalImpedanceTomography.Views;

public partial class VideoExportProgressPopup : Popup
{
    private readonly ReconstructionPageViewModel _viewModel;
    private readonly SKSize _distributionCanvasSize;
    private readonly SKSize _colorbarCanvasSize;
    private readonly SKSize _residualCanvasSize;
    private readonly PotentialDisplayMode _mode;

    private CancellationTokenSource? _cancellationTokenSource;
    private bool _closeOnCompletion;

    public VideoExportProgressPopup(ReconstructionPageViewModel viewModel,
                                    SKSize distributionCanvasSize,
                                    SKSize colorbarCanvasSize,
                                    SKSize residualCanvasSize,
                                    PotentialDisplayMode mode)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = viewModel;
        _distributionCanvasSize = distributionCanvasSize;
        _colorbarCanvasSize = colorbarCanvasSize;
        _residualCanvasSize = residualCanvasSize;
        _mode = mode;
    }

    protected override void OnOpened()
    {
        base.OnOpened();
        StartExport();
    }

    private async void StartExport()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _viewModel.BeginVideoExportProgress();

        var progress = new Progress<VideoExportProgressReport>(report =>
        {
            _viewModel.UpdateVideoExportProgress(report);
        });

        var token = _cancellationTokenSource.Token;
        VideoExportResult result;

        try
        {
            result = await _viewModel.ExportReconstructionVideoAsync(
                _distributionCanvasSize,
                _colorbarCanvasSize,
                _residualCanvasSize,
                _mode,
                progress,
                token);
        }
        catch (Exception ex)
        {
            result = VideoExportResult.CreateFailure("Export Failed", ex.Message);
        }

        _viewModel.CompleteVideoExport(result);

        if (_closeOnCompletion)
        {
            Close(new VideoExportPopupResult(result, true));
        }
    }

    private void OnAbortClicked(object sender, EventArgs e)
    {
        if (_cancellationTokenSource == null)
            return;

        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            AbortButton.IsEnabled = false;
            _viewModel.NotifyVideoExportAborting();
            _cancellationTokenSource.Cancel();
            _closeOnCompletion = true;
        }
    }

    private void OnDoneClicked(object sender, EventArgs e)
    {
        var result = _viewModel.VideoExportResult
                     ?? VideoExportResult.CreateFailure("Export Failed", "No export result was produced.");

        Close(new VideoExportPopupResult(result, false));
    }

    protected override void OnClosed()
    {
        base.OnClosed();

        if (_cancellationTokenSource != null)
        {
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch
            {
                // Ignore cancellation errors when tearing down the popup.
            }

            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }

        _viewModel.ResetVideoExportState();
    }
}

public sealed record VideoExportPopupResult(VideoExportResult Result, bool WasAborted);
