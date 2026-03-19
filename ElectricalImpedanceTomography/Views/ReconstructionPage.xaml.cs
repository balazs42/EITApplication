using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.Extensions;
using ElectricalImpedanceTomography.Controls;
using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Reconstruction.Metrics;
using Utility.Rendering;

using Workspace = Utility.Classes.Application.Workspace;
using Utility.Exports;
using CommunityToolkit.Maui.Core;
using Utility.Classes.Factories;

namespace ElectricalImpedanceTomography.Views;

public partial class ReconstructionPage : ContentPage
{
    private readonly ReconstructionPageViewModel _viewModel;
    private readonly DiscretizationCanvasRenderer _renderer = new();
    public event EventHandler<int>? PotentialModeChanged;

    private ReconstructionResult? _currentResult;
    private ReconstructionFrame? _currentFrame;
    private GradientInspectionPopup? _gradientPopup;

    // FEM transform helpers
    private float _scale, _marginX, _marginY, _meshWidth, _meshHeight, _minX, _minY, _canvasHeight;

    // hover information per canvas
    private string[]? _hoverOriginalLines; private SKPoint? _hoverOriginalPt;
    private string[]? _hoverPotentialLines; private SKPoint? _hoverPotentialPt;
    private string[]? _hoverReconstructedLines; private SKPoint? _hoverReconstructedPt;
    private string[]? _hoverAdjointLines; private SKPoint? _hoverAdjointPt;
    private string[]? _hoverInitialLines; private SKPoint? _hoverInitialPt;
    private string[]? _hoverGradientLines; private SKPoint? _hoverGradientPt;

    private PotentialDisplayMode _potMode = PotentialDisplayMode.Default;
    private ConductivityDisplayMode _conductivityMode = ConductivityDisplayMode.Classic;

    private bool _isPaused = false;
    private bool _sliderChanging = false;

    private static readonly SKColor DistributionCanvasBackgroundColor = SKColor.Parse("#1A2436");
    private static readonly SKColor ChartGradientTopColor = SKColor.Parse("#23354D");
    private static readonly SKColor ChartGradientBottomColor = SKColor.Parse("#151E2D");
    private static readonly SKColor ChartAxisColor = SKColor.Parse("#5B6F94");
    private static readonly SKColor ChartGridColor = new SKColor(255, 255, 255, 50);
    private static readonly SKColor ChartPrimaryTextColor = new SKColor(198, 212, 245);
    private static readonly SKColor ChartSecondaryTextColor = new SKColor(157, 170, 211);

    // For FEM, which node do we use as "visual ground"?
    private const int VisualReferenceNodeId = 1;

    private static readonly TrendVisualizationStyle ResidualTrendStyle = new(
        SKColor.Parse("#3A9CED"),
        new SKColor(58, 156, 237, 90),
        SKColor.Parse("#A7D2FF"),
        SKColor.Parse("#0B1C2F"));

    private static readonly TrendVisualizationStyle ErrorTrendStyle = new(
        SKColor.Parse("#F4A261"),
        new SKColor(244, 162, 97, 90),
        SKColor.Parse("#FFD8B5"),
        SKColor.Parse("#3B2A1A"));

    private static readonly TrendVisualizationStyle SimilarityTrendStyle = new(
        SKColor.Parse("#2A9D8F"),
        new SKColor(42, 157, 143, 90),
        SKColor.Parse("#9ADBD2"),
        SKColor.Parse("#103A35"));

    private readonly struct TrendVisualizationStyle
    {
        public TrendVisualizationStyle(SKColor lineColor, SKColor areaColor, SKColor pointColor, SKColor pointOutlineColor)
        {
            LineColor = lineColor;
            AreaColor = areaColor;
            PointColor = pointColor;
            PointOutlineColor = pointOutlineColor;
        }

        public SKColor LineColor { get; }
        public SKColor AreaColor { get; }
        public SKColor PointColor { get; }
        public SKColor PointOutlineColor { get; }
    }

    private static TrendVisualizationStyle ResolveTrendStyle(TrendMetricCategory category)
        => category switch
        {
            TrendMetricCategory.ErrorNorm => ErrorTrendStyle,
            TrendMetricCategory.Similarity => SimilarityTrendStyle,
            TrendMetricCategory.Residual => ResidualTrendStyle,
            _ => ResidualTrendStyle
        };

    public ReconstructionPage()
    {
        InitializeComponent();

        _viewModel = Utility.Composition.Container.ResolveObject<ReconstructionPageViewModel>();

        BindingContext = _viewModel;

        PotentialModePicker.SelectedIndexChanged += (s, e) =>
        {
            PotentialModeChanged?.Invoke(this, PotentialModePicker.SelectedIndex);
        };

        PotentialModeChanged += OnPotentialModeChanged;

        ConductivityModePicker.SelectedIndexChanged += (s, e) =>
        {
            OnConductivityModeChanged(this, ConductivityModePicker.SelectedIndex);
        };

        _viewModel.ReconstructionUpdated += OnReconstructionUpdated;
        _viewModel.ReconstructionFrameUpdated += OnReconstructionFrameUpdated;
        _viewModel.SelectedTrendMetricHistoryChanged += OnSelectedTrendMetricHistoryChanged;
        _viewModel.GradientInspectionRequested += OnGradientInspectionRequested;
        _viewModel.GradientSelectionChanged += OnGradientSelectionChanged;

        StepButton.IsEnabled = false;
        PlayButton.IsVisible = true;
        PauseButton.IsVisible = false;
        PlayerBackButton.IsEnabled = false;
        PlayerForwardButton.IsEnabled = false;

        ConductivityModePicker.SelectedIndex = (int)_conductivityMode;

        UpdateExportButtonState();

        MetricTrendCanvas.InvalidateSurface();
    }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            var (startColor, endColor) = GetBackgroundPulseColors();
            this.StartBackgroundPulse(startColor, endColor);
            _viewModel.RefreshMeasurementSourceOptions();
            _viewModel.LoadAvailableReconstructions();
            _viewModel.RefreshMethodPickerOptions();
            _viewModel.SyncInitialDistribution();
            InitialDistributionCanvas.InvalidateSurface();
            InitialColorbarCanvas.InvalidateSurface();
        }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        this.StopBackgroundPulse();
    }

    private static (Color Start, Color End) GetBackgroundPulseColors()
    {
        var theme = Application.Current?.RequestedTheme ?? AppTheme.Light;
        return theme == AppTheme.Dark
            ? (Color.FromArgb("#101B2B"), Color.FromArgb("#1A2F45"))
            : (Color.FromArgb("#F2E7D8"), Color.FromArgb("#E6DAC9"));
    }

    private IDiscretization? GetDiscretization()
        => (_currentResult?.Discretization as IDiscretization) ?? (IDiscretization?)Workspace.GetDiscretization();

    private static async Task AnimateButtonAsync(object sender)
    {
        if (sender is VisualElement ve)
        {
            await ve.ScaleTo(0.95, 50, Easing.CubicIn);
            await ve.ScaleTo(1, 50, Easing.CubicOut);
        }
    }

    private void UpdateExportButtonState()
        => ExportVideoButton.IsEnabled = Workspace.GetReconstructionFrames().Count > 0;

    private void OnInitialDistributionEdited(object? sender, EventArgs e)
    {
        _viewModel.AcknowledgeInitialDistributionUpdate();
        InitialDistributionCanvas.InvalidateSurface();
        InitialColorbarCanvas.InvalidateSurface();
    }

    private void OnInitialDistributionPickerChanged(object? sender, EventArgs e)
    {
        _viewModel.SyncInitialDistribution(true);
        _viewModel.AcknowledgeInitialDistributionUpdate();
        InitialDistributionCanvas.InvalidateSurface();
        InitialColorbarCanvas.InvalidateSurface();
    }

    #region Simulation control
    private async void OnPlayButtonClicked(object sender, EventArgs e)
    {
        if(GetDiscretization() == null)
        {
            await DisplayAlert("No Mesh", "You should create or load a mesh to start reconstrucion!", "Ok");
            return;
        }

        if(!_viewModel.CheckReconstructionMethodAgainstDiscretization())
        {
            await DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver that your discretization is made for!", "Ok");
            return;
        }

        await AnimateButtonAsync(sender);
        StepButton.IsEnabled = false;
        PlayerBackButton.IsEnabled = false;
        PlayerForwardButton.IsEnabled = false;
        PlayButton.IsEnabled = false;
        PlayButton.IsVisible = false;
        PauseButton.IsVisible = true;
        PauseButton.IsEnabled = true;
        _isPaused = false;

        _viewModel.PrepareForNewReconstruction();
        UpdateExportButtonState();
        _viewModel.BeginReconstructionMetrics();

        int iterations = _viewModel.MaxIterationCount;
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                await _viewModel.RunFullReconstructionCycleAsync();
            }
        }
        finally
        {
            _viewModel.PauseReconstructionMetrics();
            _isPaused = true;
            StepButton.IsEnabled = true;
            PlayerBackButton.IsEnabled = true;
            PlayerForwardButton.IsEnabled = true;
            PlayButton.IsEnabled = true;
            PlayButton.IsVisible = true;
            PauseButton.IsVisible = false;
        }
    }

    private async void OnEditInitialDistributionClicked(object sender, EventArgs e)
    {
        if (!_viewModel.CanEditInitialDistribution)
            return;

        var discretization = GetDiscretization();
        if (discretization == null)
        {
            await DisplayAlert("No Mesh", "You should create or load a mesh before editing the initial distribution!", "Ok");
            return;
        }

        var initial = Workspace.GetInitialConductivityDistribution() ?? discretization.GetConductivityDistribution();
        var original = Workspace.GetOriginalConductivityDistribution();
        var popup = new InitialDistributionEditorPopup(discretization,
                                                       initial,
                                                       original,
                                                       _viewModel.ReconstructionParameters.InitialDistributionType);
        popup.DistributionChanged += OnInitialDistributionEdited;
        await this.ShowPopupAsync(popup);
        popup.DistributionChanged -= OnInitialDistributionEdited;

        InitialDistributionCanvas.InvalidateSurface();
        InitialColorbarCanvas.InvalidateSurface();
    }

    private async void OnPauseButtonClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);
        _viewModel.PauseReconstruction();
        _isPaused = true;
        StepButton.IsEnabled = true;
        PlayerBackButton.IsEnabled = true;
        PlayerForwardButton.IsEnabled = true;
        PlayButton.IsVisible = true;
        PauseButton.IsVisible = false;
    }

    private async void OnStepButtonClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);
        if (!_isPaused)
            return;

        await _viewModel.StepReconstructionAsync();
    }

    private async void OnStopButtonClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);
        _viewModel.StopReconstruction();
        _isPaused = false;
        StepButton.IsEnabled = false;
        PlayerBackButton.IsEnabled = false;
        PlayerForwardButton.IsEnabled = false;
        PlayButton.IsEnabled = true;
        PlayButton.IsVisible = true;
        PauseButton.IsVisible = false;
        UpdateExportButtonState();
    }
    #endregion

    private async void OnPlayerBackButtontapped(object sender, TappedEventArgs e)
    {
        if (!_isPaused)
            return;

        await AnimateButtonAsync(sender);
        var frames = Workspace.GetReconstructionFrames();
        int index = (int)Math.Round(PlaybackSlider.Value);
        if (index > 0)
            PlaybackSlider.Value = index - 1;
    }

    private async void OnPlayerForwardButtontapped(object sender, TappedEventArgs e)
    {
        if (!_isPaused)
            return;

        await AnimateButtonAsync(sender);
        var frames = Workspace.GetReconstructionFrames();
        int index = (int)Math.Round(PlaybackSlider.Value);
        if (index < frames.Count - 1)
            PlaybackSlider.Value = index + 1;
    }


    private void OnReconstructionUpdated(object? sender, ReconstructionResult result)
    {
        _currentResult = result;
        _currentFrame = result.Frames.LastOrDefault();
        var frames = Workspace.GetReconstructionFrames();
        Dispatcher.Dispatch(() =>
        {
            _sliderChanging = true;
            if (frames.Count > 0)
            {
                PlaybackSlider.Maximum = frames.Count - 1;
                PlaybackSlider.Value = PlaybackSlider.Maximum;
            }
            _sliderChanging = false;
            InvalidateAll();
            UpdatePlaybackLabel();
            UpdateExportButtonState();
        });
    }

    private void OnReconstructionFrameUpdated(object? sender, ReconstructionFrame frame)
    {
        _currentFrame = frame;
        var frames = Workspace.GetReconstructionFrames();
        Dispatcher.Dispatch(() =>
        {
            _sliderChanging = true;
            if (frames.Count > 0)
            {
                PlaybackSlider.Maximum = frames.Count - 1;
                PlaybackSlider.Value = PlaybackSlider.Maximum;
            }
            _sliderChanging = false;

            PotentialDistributionCanvas.InvalidateSurface();
            AdjointDistributionCanvas.InvalidateSurface();
            GradientDistributionCanvas.InvalidateSurface();
            PotentialColorbarCanvas.InvalidateSurface();
            AdjointColorbarCanvas.InvalidateSurface();
            GradientColorbarCanvas.InvalidateSurface();

            UpdatePlaybackLabel();
            UpdateExportButtonState();
        });
    }

    private void OnSelectedTrendMetricHistoryChanged(object? sender, EventArgs e)
        => MainThread.BeginInvokeOnMainThread(() => MetricTrendCanvas.InvalidateSurface());

    private async void OnGradientInspectionRequested(object? sender, EventArgs e)
    {
        if (_gradientPopup != null)
            return;

        var popup = new GradientInspectionPopup(_viewModel);
        _gradientPopup = popup;
        popup.Closed += OnGradientPopupClosed;
        await this.ShowPopupAsync(popup);
    }

    private void OnGradientPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        if (_gradientPopup is GradientInspectionPopup popup)
        {
            popup.Closed -= OnGradientPopupClosed;
            _gradientPopup = null;
        }
    }

    private void OnGradientSelectionChanged(object? sender, int index)
    {
        if (index < 0)
            return;

        var sample = _viewModel.GetGradientSample(index);
        if (sample is null)
            return;

        Dispatcher.Dispatch(() =>
        {
            double target = sample.FrameIndex;
            if (Math.Abs(PlaybackSlider.Value - target) < 0.01)
                return;

            _sliderChanging = true;
            PlaybackSlider.Value = Math.Clamp(target, PlaybackSlider.Minimum, PlaybackSlider.Maximum);
            _sliderChanging = false;
            UpdatePlaybackLabel();
        });
    }

    #region Drawing helpers
    private void ComputeFemTransform(FEMMesh mesh, SKImageInfo info)
    {
        const float pad = 10f;
        float availW = info.Width - 2 * pad;
        float availH = info.Height - 2 * pad;

        _minX = (float)mesh.Vertices.Min(v => v.X);
        _minY = (float)mesh.Vertices.Min(v => v.Y);
        float maxX = (float)mesh.Vertices.Max(v => v.X);
        float maxY = (float)mesh.Vertices.Max(v => v.Y);
        _meshWidth = maxX - _minX;
        _meshHeight = maxY - _minY;
        _scale = Math.Min(availW / _meshWidth, availH / _meshHeight);
        float usedW = _meshWidth * _scale;
        float usedH = _meshHeight * _scale;
        _marginX = pad + (availW - usedW) / 2f;
        _marginY = pad + (availH - usedH) / 2f;
        _canvasHeight = info.Height;
    }

    private SKPoint ToCanvas(FEMVertex v)
        => new SKPoint((float)(v.X - _minX) * _scale + _marginX,
                       _canvasHeight - ((float)(v.Y - _minY) * _scale + _marginY));

    private float Dot(SKPoint a, SKPoint b) => a.X * b.X + a.Y * b.Y;

    private bool PointInTriangle(SKPoint p, SKPoint a, SKPoint b, SKPoint c,
                                 out float u, out float v, out float w)
    {
        var v0 = b - a; var v1 = c - a; var v2 = p - a;
        float d00 = Dot(v0, v0); float d01 = Dot(v0, v1); float d11 = Dot(v1, v1);
        float d20 = Dot(v2, v0); float d21 = Dot(v2, v1); float denom = d00 * d11 - d01 * d01;
        v = (d11 * d20 - d01 * d21) / denom;
        w = (d00 * d21 - d01 * d20) / denom;
        u = 1 - v - w;
        return (u >= 0) && (v >= 0) && (w >= 0);
    }

    private void DrawHoverInfo(SKCanvas canvas, SKImageInfo info, string[]? lines, SKPoint? pt)
    {
        if (lines == null || !pt.HasValue) return;
        using var txt = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var font = new SKFont(SKTypeface.Default, 14);
        float w = lines.Max(l => font.MeasureText(l)) + 8;
        float h = lines.Length * (font.Size + 4) + 4;
        var center = new SKPoint(info.Width / 2f, info.Height / 2f);
        var dir = new SKPoint(center.X - pt.Value.X, center.Y - pt.Value.Y);
        const float off = 8f;
        float x = dir.X > 0 ? pt.Value.X + off : pt.Value.X - off - w;
        float y = dir.Y > 0 ? pt.Value.Y + off : pt.Value.Y - off - h;
        var box = new SKRect(x, y, x + w, y + h);
        using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 200), IsAntialias = true };
        canvas.DrawRoundRect(box, 4, 4, bg);
        float ty = box.Top + font.Size + 2;
        foreach (var line in lines)
        {
            canvas.DrawText(line, box.Left + 4, ty, SKTextAlign.Left, font, txt);
            ty += font.Size + 4;
        }
    }

    private DiscretizationCanvasRenderOptions CreateConductivityRenderOptions()
        => new()
        {
            BackgroundColor = DistributionCanvasBackgroundColor,
            ConductivityDisplayMode = _conductivityMode
        };

    private DiscretizationCanvasRenderOptions CreatePotentialRenderOptions()
        => new()
        {
            BackgroundColor = DistributionCanvasBackgroundColor,
            PotentialDisplayMode = _potMode,
            VisualReferenceNodeId = VisualReferenceNodeId
        };

    private DiscretizationColorBarOptions CreateHorizontalColorBarOptions()
        => new()
        {
            BackgroundColor = DistributionCanvasBackgroundColor,
            Orientation = ColorBarOrientation.Horizontal,
            TextColor = SKColors.White
        };
    #endregion

    #region Canvas paint
    private void OnMetricTrendCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var key = _viewModel.SelectedTrendMetricKey;
        var history = _viewModel.GetTrendHistorySnapshot(key);
        var metric = _viewModel.GetMetricByKey(key);
        string metricName = metric?.Name ?? "Metric";
        var style = ResolveTrendStyle(metric?.TrendCategory ?? TrendMetricCategory.Residual);

        string lastFormattedValue = "—";
        int lastIteration = history.Count;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            double value = history[i];
            if (double.IsNaN(value) || double.IsInfinity(value))
                continue;

            lastFormattedValue = _viewModel.FormatTrendValue(key, value);
            lastIteration = i + 1;
            break;
        }

        DrawMetricTrend(e.Surface.Canvas, e.Info, history, metricName, lastFormattedValue, lastIteration, style);
    }

    private void DrawMetricTrend(SKCanvas canvas,
                                 SKImageInfo info,
                                 IReadOnlyList<double> history,
                                 string metricName,
                                 string lastFormattedValue,
                                 int lastIteration,
                                 TrendVisualizationStyle style)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);

        double minValue = double.PositiveInfinity;
        double maxValue = double.NegativeInfinity;
        foreach (double value in history)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                continue;

            if (value < minValue) minValue = value;
            if (value > maxValue) maxValue = value;
        }

        if (double.IsPositiveInfinity(minValue) || double.IsNegativeInfinity(maxValue))
        {
            using var emptyPaint = new SKPaint
            {
                Color = ChartSecondaryTextColor,
                TextSize = 14,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText($"No data for {metricName}", info.Width / 2f, info.Height / 2f, emptyPaint);
            return;
        }

        if (Math.Abs(maxValue - minValue) < 1e-12)
            maxValue = minValue + 1e-12;

        const float leftPadding = 64f;
        const float rightPadding = 28f;
        const float topPadding = 24f;
        const float bottomPadding = 64f;

        float chartWidth = info.Width - leftPadding - rightPadding;
        float chartHeight = info.Height - topPadding - bottomPadding;
        if (chartWidth <= 0 || chartHeight <= 0)
            return;

        var chartRect = SKRect.Create(leftPadding, topPadding, chartWidth, chartHeight);

        using var chartBackgroundPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(chartRect.Left, chartRect.Top),
                                                   new SKPoint(chartRect.Left, chartRect.Bottom),
                                                   new[] { ChartGradientTopColor, ChartGradientBottomColor },
                                                   null,
                                                   SKShaderTileMode.Clamp)
        };
        canvas.DrawRoundRect(chartRect, 10f, 10f, chartBackgroundPaint);

        using var gridPaint = new SKPaint
        {
            Color = ChartGridColor,
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 6, 6 }, 0)
        };

        using var axisPaint = new SKPaint
        {
            Color = ChartAxisColor,
            StrokeWidth = 2,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        using var tickPaint = new SKPaint
        {
            Color = ChartAxisColor,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        using var valuePaint = new SKPaint
        {
            Color = ChartSecondaryTextColor,
            TextSize = 12,
            IsAntialias = true,
            TextAlign = SKTextAlign.Right
        };

        using var bottomLabelPaint = new SKPaint
        {
            Color = ChartSecondaryTextColor,
            TextSize = 12,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        using var axisLabelPaint = new SKPaint
        {
            Color = ChartPrimaryTextColor,
            TextSize = 14,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        int horizontalLines = 4;
        for (int i = 0; i <= horizontalLines; i++)
        {
            float t = i / (float)horizontalLines;
            float y = chartRect.Top + chartRect.Height * t;
            canvas.DrawLine(chartRect.Left, y, chartRect.Right, y, gridPaint);

            double value = maxValue - (maxValue - minValue) * t;
            canvas.DrawText(value.ToString("G4", CultureInfo.InvariantCulture),
                            chartRect.Left - 10f,
                            y + valuePaint.TextSize / 3f,
                            valuePaint);
        }

        int count = history.Count;
        int verticalLines = Math.Min(count - 1, 5);
        if (verticalLines > 0)
        {
            for (int i = 0; i <= verticalLines; i++)
            {
                float t = i / (float)verticalLines;
                float x = chartRect.Left + chartRect.Width * t;
                canvas.DrawLine(x, chartRect.Top, x, chartRect.Bottom, gridPaint);
                canvas.DrawLine(x, chartRect.Bottom, x, chartRect.Bottom + 6f, tickPaint);

                int iteration = (int)Math.Round(1 + t * (count - 1));
                canvas.DrawText(iteration.ToString(), x, chartRect.Bottom + bottomLabelPaint.TextSize + 14f, bottomLabelPaint);
            }
        }
        else
        {
            canvas.DrawLine(chartRect.Left, chartRect.Top, chartRect.Left, chartRect.Bottom, gridPaint);
            canvas.DrawLine(chartRect.Left, chartRect.Bottom, chartRect.Left, chartRect.Bottom + 6f, tickPaint);
            canvas.DrawText("1", chartRect.Left, chartRect.Bottom + bottomLabelPaint.TextSize + 14f, bottomLabelPaint);
        }

        var origin = new SKPoint(chartRect.Left, chartRect.Bottom);
        canvas.DrawLine(origin, new SKPoint(chartRect.Right, chartRect.Bottom), axisPaint);
        canvas.DrawLine(origin, new SKPoint(chartRect.Left, chartRect.Top), axisPaint);

        using var linePaint = new SKPaint
        {
            Color = style.LineColor,
            StrokeWidth = 3,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var areaPaint = new SKPaint
        {
            Color = style.AreaColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var pointPaint = new SKPaint
        {
            Color = style.PointColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var pointOutlinePaint = new SKPaint
        {
            Color = style.PointOutlineColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        var linePath = new SKPath();
        var areaPath = new SKPath();
        areaPath.MoveTo(origin);

        float step = count > 1 ? chartRect.Width / (count - 1) : 0f;
        SKPoint lastPoint = origin;
        bool hasStarted = false;

        for (int i = 0; i < count; i++)
        {
            double metricValue = history[i];
            if (double.IsNaN(metricValue) || double.IsInfinity(metricValue))
                continue;

            float x = chartRect.Left + (count > 1 ? step * i : chartRect.Width / 2f);
            double normalized = (metricValue - minValue) / (maxValue - minValue);
            float y = chartRect.Top + chartRect.Height * (float)(1 - normalized);

            if (!hasStarted)
            {
                linePath.MoveTo(x, y);
                hasStarted = true;
            }
            else
                linePath.LineTo(x, y);

            areaPath.LineTo(x, y);

            canvas.DrawCircle(x, y, 4f, pointPaint);
            canvas.DrawCircle(x, y, 4f, pointOutlinePaint);

            lastPoint = new SKPoint(x, y);
        }

        if (!hasStarted)
            return;

        areaPath.LineTo(lastPoint.X, origin.Y);
        areaPath.Close();

        canvas.DrawPath(areaPath, areaPaint);
        canvas.DrawPath(linePath, linePaint);

        canvas.DrawText("Iteration", chartRect.MidX, origin.Y + axisLabelPaint.TextSize + 26f, axisLabelPaint);

        canvas.Save();
        canvas.Translate(chartRect.Left - 44f, chartRect.MidY);
        canvas.RotateDegrees(-90);
        canvas.DrawText(metricName, 0, 0, axisLabelPaint);
        canvas.Restore();

        using var annotationPaint = new SKPaint
        {
            Color = ChartPrimaryTextColor,
            TextSize = 11,
            IsAntialias = true
        };
        using var annotationBackgroundPaint = new SKPaint
        {
            Color = new SKColor(20, 30, 46, 220),
            IsAntialias = true
        };
        using var annotationBorderPaint = new SKPaint
        {
            Color = style.LineColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            IsAntialias = true
        };

        string lastLabel = lastIteration > 0
            ? $"Iter {lastIteration}: {lastFormattedValue}"
            : $"Iter {count}: {lastFormattedValue}";
        float labelWidth = annotationPaint.MeasureText(lastLabel);
        const float annotationMargin = 16f;
        const float annotationPaddingX = 10f;
        const float annotationPaddingY = 8f;
        float bubbleWidth = labelWidth + annotationPaddingX * 2f;
        float bubbleHeight = annotationPaint.TextSize + annotationPaddingY * 2f;
        float preferredLeft = chartRect.Right - bubbleWidth - annotationMargin;
        float minLeft = chartRect.Left + annotationMargin;
        float bubbleLeft = Math.Max(minLeft, preferredLeft);
        float bubbleTop = chartRect.Top + annotationMargin;
        var bubbleRect = new SKRect(bubbleLeft,
                                    bubbleTop,
                                    bubbleLeft + bubbleWidth,
                                    bubbleTop + bubbleHeight);

        canvas.DrawRoundRect(bubbleRect, 8f, 8f, annotationBackgroundPaint);
        canvas.DrawRoundRect(bubbleRect, 8f, 8f, annotationBorderPaint);
        float textX = bubbleRect.Left + annotationPaddingX;
        float textY = bubbleRect.Top + annotationPaddingY + annotationPaint.TextSize;
        canvas.DrawText(lastLabel, textX, textY, annotationPaint);
    }

    private void OnOriginalCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var discretization = GetDiscretization();
        var cd = _currentResult?.OriginalConductivityDistribution
                 ?? Workspace.GetOriginalConductivityDistribution()
                 ?? discretization?.GetConductivityDistribution();
        _renderer.Draw(e.Surface.Canvas,
                       e.Info,
                       new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Conductivity, cd),
                       CreateConductivityRenderOptions());
        DrawHoverInfo(e.Surface.Canvas, e.Info, _hoverOriginalLines, _hoverOriginalPt);
    }

    private void OnPotentialCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var pd = _currentFrame?.CalculatedPotentialDistribution ?? discretization?.GetPotentialDistribution();
        _renderer.Draw(e.Surface.Canvas,
                       e.Info,
                       new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Potential, PotentialDistribution: pd),
                       CreatePotentialRenderOptions());
        DrawHoverInfo(e.Surface.Canvas, e.Info, _hoverPotentialLines, _hoverPotentialPt);
    }

    private void OnReconstructedCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var cd = _currentResult?.ReconstructedConductivityDistribution ?? discretization?.GetConductivityDistribution();
        _renderer.Draw(e.Surface.Canvas,
                       e.Info,
                       new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Conductivity, cd),
                       CreateConductivityRenderOptions());
        DrawHoverInfo(e.Surface.Canvas, e.Info, _hoverReconstructedLines, _hoverReconstructedPt);
    }

    private void OnAdjointCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var pd = _currentFrame?.CalculatedAdjointDistribution ?? discretization?.GetPotentialDistribution();
        _renderer.Draw(e.Surface.Canvas,
                       e.Info,
                       new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Potential, PotentialDistribution: pd),
                       CreatePotentialRenderOptions());
        DrawHoverInfo(e.Surface.Canvas, e.Info, _hoverAdjointLines, _hoverAdjointPt);
    }

    private void OnInitialCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var cd = _currentResult?.InitialConductivitiyDistribution ?? discretization?.GetConductivityDistribution();
        _renderer.Draw(e.Surface.Canvas,
                       e.Info,
                       new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Conductivity, cd),
                       CreateConductivityRenderOptions());
        DrawHoverInfo(e.Surface.Canvas, e.Info, _hoverInitialLines, _hoverInitialPt);
    }

    private void OnGradientCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var cd = _currentFrame?.ConductivityGradient;
        _renderer.Draw(e.Surface.Canvas,
                       e.Info,
                       new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Conductivity, cd),
                       CreateConductivityRenderOptions());
        DrawHoverInfo(e.Surface.Canvas, e.Info, _hoverGradientLines, _hoverGradientPt);
    }

    private void OnOriginalColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var discretization = GetDiscretization();
        var cd = _currentResult?.OriginalConductivityDistribution
                 ?? Workspace.GetOriginalConductivityDistribution()
                 ?? discretization?.GetConductivityDistribution();
        _renderer.DrawColorBar(e.Surface.Canvas,
                               e.Info,
                               new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Conductivity, cd),
                               CreateConductivityRenderOptions(),
                               CreateHorizontalColorBarOptions());
    }


    private void OnPotentialColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var pd = _currentFrame?.CalculatedPotentialDistribution ?? discretization?.GetPotentialDistribution();
        _renderer.DrawColorBar(e.Surface.Canvas,
                               e.Info,
                               new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Potential, PotentialDistribution: pd),
                               CreatePotentialRenderOptions(),
                               CreateHorizontalColorBarOptions());
    }


    private void OnReconstructedColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var cd = _currentResult?.ReconstructedConductivityDistribution ?? discretization?.GetConductivityDistribution();
        _renderer.DrawColorBar(e.Surface.Canvas,
                               e.Info,
                               new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Conductivity, cd),
                               CreateConductivityRenderOptions(),
                               CreateHorizontalColorBarOptions());
    }

    private void OnAdjointColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var pd = _currentFrame?.CalculatedAdjointDistribution ?? discretization?.GetPotentialDistribution();
        _renderer.DrawColorBar(e.Surface.Canvas,
                               e.Info,
                               new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Potential, PotentialDistribution: pd),
                               CreatePotentialRenderOptions(),
                               CreateHorizontalColorBarOptions());
    }

    private void OnInitialColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var cd = _currentResult?.InitialConductivitiyDistribution ?? discretization?.GetConductivityDistribution();
        _renderer.DrawColorBar(e.Surface.Canvas,
                               e.Info,
                               new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Conductivity, cd),
                               CreateConductivityRenderOptions(),
                               CreateHorizontalColorBarOptions());
    }

    private void OnGradientColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var cd = _currentFrame?.ConductivityGradient;
        _renderer.DrawColorBar(e.Surface.Canvas,
                               e.Info,
                               new DiscretizationRenderRequest(GetDiscretization(), DiscretizationRenderMode.Conductivity, cd),
                               CreateConductivityRenderOptions(),
                               CreateHorizontalColorBarOptions());
    }
    #endregion

    #region Touch handlers
    private async void OnOriginalCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
        { await ShowDisplayModeMenu(); e.Handled = true; return; }
        var discretization = GetDiscretization();
        if (discretization == null) return;
        var view = (SKCanvasView)sender;
        if (discretization is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            {
                _hoverOriginalLines = null; _hoverOriginalPt = null; view.InvalidateSurface(); e.Handled = true; return;
            }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            _hoverOriginalLines = null; _hoverOriginalPt = null;
            var cd = (_currentResult?.OriginalConductivityDistribution ?? Workspace.GetOriginalConductivityDistribution() ?? fem.GetConductivityDistribution());
            foreach (var elem in fem.GetElements().Cast<FEMElement>())
            {
                var c0 = ToCanvas(elem.Vertices[0]);
                var c1 = ToCanvas(elem.Vertices[1]);
                var c2 = ToCanvas(elem.Vertices[2]);
                if (PointInTriangle(e.Location, c0, c1, c2, out _, out _, out _))
                {
                    double val = cd.GetConductivity(elem.Id);
                    _hoverOriginalLines = new[] { $"Elem: {elem.Id}", $"σ: {val:F3}" };
                    _hoverOriginalPt = e.Location;
                    break;
                }
            }
            view.InvalidateSurface(); e.Handled = true;
        }
        else if (discretization is LBMGrid lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx;
            float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverOriginalLines = null; _hoverOriginalPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var cd = (_currentResult?.OriginalConductivityDistribution ?? Workspace.GetOriginalConductivityDistribution() ?? lbm.GetConductivityDistribution());
            double val = cd.Conductivities[el.Id];
            _hoverOriginalLines = new[] { $"ID: {el.Id}", $"σ: {val:F3}" };
            _hoverOriginalPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
    }

    private async void OnPotentialCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
        { await ShowDisplayModeMenu(); e.Handled = true; return; }
        var discretization = GetDiscretization(); if (discretization == null) return; var view = (SKCanvasView)sender;
        if (discretization is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            { _hoverPotentialLines = null; _hoverPotentialPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            var verts = fem.Vertices;
            var nearest = verts.OrderBy(v => (ToCanvas(v) - e.Location).LengthSquared).First();
            var pd = _currentFrame?.CalculatedPotentialDistribution ?? fem.GetPotentialDistribution();
            double val = pd.GetPotential(nearest.GlobalId);
            _hoverPotentialLines = new[] { $"GID: {nearest.GlobalId}", $"Φ: {val:F3}" };
            _hoverPotentialPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
        else if (discretization is LBMGrid lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx;
            float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverPotentialLines = null; _hoverPotentialPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var pd = _currentFrame?.CalculatedPotentialDistribution ?? lbm.GetPotentialDistribution();
            double val = pd.Potentials[el.Id];
            _hoverPotentialLines = new[] { $"ID: {el.Id}", $"Φ: {val:F3}" };
            _hoverPotentialPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
    }

    private async void OnReconstructedCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
        { await ShowDisplayModeMenu(); e.Handled = true; return; }
        var discretization = GetDiscretization(); if (discretization == null) return; var view = (SKCanvasView)sender;
        if (discretization is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            { _hoverReconstructedLines = null; _hoverReconstructedPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            _hoverReconstructedLines = null;
            var cd = (_currentResult?.ReconstructedConductivityDistribution ?? fem.GetConductivityDistribution());
            foreach (var elem in fem.GetElements().Cast<FEMElement>())
            {
                var c0 = ToCanvas(elem.Vertices[0]);
                var c1 = ToCanvas(elem.Vertices[1]);
                var c2 = ToCanvas(elem.Vertices[2]);
                if (PointInTriangle(e.Location, c0, c1, c2, out _, out _, out _))
                {
                    double val = cd.GetConductivity(elem.Id);
                    _hoverReconstructedLines = new[] { $"Elem: {elem.Id}", $"σ: {val:F3}" };
                    _hoverReconstructedPt = e.Location;
                    break;
                }
            }
            view.InvalidateSurface(); e.Handled = true;
        }
        else if (discretization is LBMGrid lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx;
            float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverReconstructedLines = null; _hoverReconstructedPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var cd = (_currentResult?.ReconstructedConductivityDistribution ?? lbm.GetConductivityDistribution());
            double val = cd.Conductivities[el.Id];
            _hoverReconstructedLines = new[] { $"ID: {el.Id}", $"σ: {val:F3}" };
            _hoverReconstructedPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
    }

    private async void OnAdjointCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
        { await ShowDisplayModeMenu(); e.Handled = true; return; }
        var discretization = GetDiscretization(); if (discretization == null) return; var view = (SKCanvasView)sender;
        if (discretization is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            { _hoverAdjointLines = null; _hoverAdjointPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            var verts = fem.Vertices;
            var nearest = verts.OrderBy(v => (ToCanvas(v) - e.Location).LengthSquared).First();
            var pd = (_currentFrame?.CalculatedAdjointDistribution ?? fem.GetPotentialDistribution());
            double val = pd.GetPotential(nearest.GlobalId);
            _hoverAdjointLines = new[] { $"GID: {nearest.GlobalId}", $"Φ: {val:F3}" };
            _hoverAdjointPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
        else if (discretization is LBMGrid lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx; float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverAdjointLines = null; _hoverAdjointPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var pd = (_currentFrame?.CalculatedAdjointDistribution ?? lbm.GetPotentialDistribution());
            double val = pd.Potentials[el.Id];
            _hoverAdjointLines = new[] { $"ID: {el.Id}", $"Φ: {val:F3}" };
            _hoverAdjointPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
    }

    private async void OnInitialCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
        { await ShowDisplayModeMenu(); e.Handled = true; return; }
        var discretization = GetDiscretization(); if (discretization == null) return; var view = (SKCanvasView)sender;
        if (discretization is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            { _hoverInitialLines = null; _hoverInitialPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            _hoverInitialLines = null;
            var cd = (_currentResult?.InitialConductivitiyDistribution ?? fem.GetConductivityDistribution());
            foreach (var elem in fem.GetElements().Cast<FEMElement>())
            {
                var c0 = ToCanvas(elem.Vertices[0]);
                var c1 = ToCanvas(elem.Vertices[1]);
                var c2 = ToCanvas(elem.Vertices[2]);
                if (PointInTriangle(e.Location, c0, c1, c2, out _, out _, out _))
                {
                    double val = cd.GetConductivity(elem.Id);
                    _hoverInitialLines = new[] { $"Elem: {elem.Id}", $"σ: {val:F3}" };
                    _hoverInitialPt = e.Location;
                    break;
                }
            }
            view.InvalidateSurface(); e.Handled = true;
        }
        else if (discretization is LBMGrid lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx; float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverInitialLines = null; _hoverInitialPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var cd = (_currentResult?.InitialConductivitiyDistribution ?? lbm.GetConductivityDistribution());
            double val = cd.Conductivities[el.Id];
            _hoverInitialLines = new[] { $"ID: {el.Id}", $"σ: {val:F3}" };
            _hoverInitialPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
    }

    private async void OnGradientCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
        { await ShowDisplayModeMenu(); e.Handled = true; return; }
        var discretization = GetDiscretization(); if (discretization == null) return; var view = (SKCanvasView)sender;
        if (discretization is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            { _hoverGradientLines = null; _hoverGradientPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            _hoverGradientLines = null;
            var cd = _currentFrame?.ConductivityGradient;
            if (cd != null)
            {
                foreach (var elem in fem.GetElements().Cast<FEMElement>())
                {
                    var c0 = ToCanvas(elem.Vertices[0]);
                    var c1 = ToCanvas(elem.Vertices[1]);
                    var c2 = ToCanvas(elem.Vertices[2]);
                    if (PointInTriangle(e.Location, c0, c1, c2, out _, out _, out _))
                    {
                        double val = cd.GetConductivity(elem.Id);
                        _hoverGradientLines = new[] { $"Elem: {elem.Id}", $"∂σ: {val:F3}" };
                        _hoverGradientPt = e.Location;
                        break;
                    }
                }
            }
            view.InvalidateSurface(); e.Handled = true;
        }
        else if (discretization is LBMGrid lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx; float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverGradientLines = null; _hoverGradientPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var cd = _currentFrame?.ConductivityGradient;
            if (cd != null)
            {
                double val = cd.Conductivities[el.Id];
                _hoverGradientLines = new[] { $"ID: {el.Id}", $"∂σ: {val:F3}" };
                _hoverGradientPt = e.Location;
            }
            view.InvalidateSurface(); e.Handled = true;
        }
    }
    #endregion

    private async Task ShowDisplayModeMenu()
    {
        string? choice = await DisplayActionSheet("Select Potential Display Mode", "Cancel", null,
            Enum.GetNames(typeof(PotentialDisplayMode)));
        if (Enum.TryParse(choice, out PotentialDisplayMode mode))
            PotentialModePicker.SelectedIndex = (int)mode;
    }

    private void InvalidateAll()
    {
        OriginalDistributionCanvas.InvalidateSurface();
        InitialDistributionCanvas.InvalidateSurface();
        ReconstructedDistributionCanvas.InvalidateSurface();
        PotentialDistributionCanvas.InvalidateSurface();
        AdjointDistributionCanvas.InvalidateSurface();
        GradientDistributionCanvas.InvalidateSurface();
        OriginalColorbarCanvas.InvalidateSurface();
        InitialColorbarCanvas.InvalidateSurface();
        ReconstructedColorbarCanvas.InvalidateSurface();
        PotentialColorbarCanvas.InvalidateSurface();
        AdjointColorbarCanvas.InvalidateSurface();
        GradientColorbarCanvas.InvalidateSurface();
    }

    private void InvalidateConductivityDisplays()
    {
        OriginalDistributionCanvas.InvalidateSurface();
        InitialDistributionCanvas.InvalidateSurface();
        ReconstructedDistributionCanvas.InvalidateSurface();
        GradientDistributionCanvas.InvalidateSurface();
        OriginalColorbarCanvas.InvalidateSurface();
        InitialColorbarCanvas.InvalidateSurface();
        ReconstructedColorbarCanvas.InvalidateSurface();
        GradientColorbarCanvas.InvalidateSurface();
    }

    private async void OnExportClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);

        ExportVideoButton.IsEnabled = false;

        var choice = await this.ShowPopupAsync(new ExportOptionsPopup());

        switch (choice)
        {
            case ExportMode.Video:
                await HandleVideoExportAsync();
                break;
            case ExportMode.Csv:
                await HandleCsvExportAsync();
                break;
            default:
                UpdateExportButtonState();
                break;
        }
    }

    private async Task HandleVideoExportAsync()
    {
        var popup = new VideoExportProgressPopup(_viewModel,
            PotentialDistributionCanvas.CanvasSize,
            PotentialColorbarCanvas.CanvasSize,
            MetricTrendCanvas.CanvasSize,
            _potMode);

        var popupResult = await this.ShowPopupAsync(popup) as VideoExportPopupResult;

        UpdateExportButtonState();

        if (popupResult is { WasAborted: true, Result: var aborted })
        {
            string title = aborted.ErrorTitle ?? "Export Aborted";
            string message = aborted.ErrorMessage ?? "The video export was aborted.";
            await DisplayAlert(title, message, "OK");
        }
        else if (popupResult is { Result.Success: false, WasAborted: false })
        {
            var failure = popupResult.Result;
            string title = failure.ErrorTitle ?? "Export Failed";
            string message = failure.ErrorMessage ?? "Unknown error.";
            await DisplayAlert(title, message, "OK");
        }
    }

    private async Task HandleCsvExportAsync()
    {
        try
        {
            var exportResult = await _viewModel.ExportReconstructionDataAsync(_potMode, _conductivityMode);

            UpdateExportButtonState();

            if (exportResult.Success)
            {
                string message = $"Data saved to:\n{exportResult.DirectoryPath}";
                await DisplayAlert("Export Complete", message, "OK");
            }
            else
            {
                string title = exportResult.ErrorTitle ?? "Export Failed";
                string message = exportResult.ErrorMessage ?? "Unknown error.";
                await DisplayAlert(title, message, "OK");
            }
        }
        catch (Exception ex)
        {
            UpdateExportButtonState();
            await DisplayAlert("Export Failed", ex.Message, "OK");
        }
    }

    private void OnSaveClicked(object sender, EventArgs e) => _viewModel.SaveReconstruction();
    private void OnLoadClicked(object sender, EventArgs e) => _viewModel.LoadAvailableReconstructions();

    private void OnReconstructionSelected(object sender, TappedEventArgs e)
    {
        if (sender is Border border && border.BindingContext is ReconstructionInfo info)
        {
            _viewModel.LoadReconstruction(info.FilePath);
            var results = Workspace.GetReconstructionResults();

            _currentResult = results.LastOrDefault();
            var frames = Workspace.GetReconstructionFrames();

            _currentFrame = frames.LastOrDefault();

            PlaybackSlider.Maximum = frames.Count > 0 ? frames.Count - 1 : 0;
            PlaybackSlider.Value = PlaybackSlider.Maximum;

            if (_currentResult != null)
            {
                _viewModel.IterationCount = results.Count;
                _viewModel.Residual = ReconstructionStatistics.CalculateResidual(_currentResult);
            }
            Dispatcher.Dispatch(() =>
            {
                InvalidateAll();
                UpdatePlaybackLabel();
                UpdateExportButtonState();
            });
        }
    }

    private void OnPlayPauseAcceleratorInvoked(object sender, EventArgs e)
    {
        if (PlayButton.IsVisible)
            OnPlayButtonClicked(PlayButton, EventArgs.Empty);
        else
            OnPauseButtonClicked(PauseButton, EventArgs.Empty);
    }

    private void OnStepAcceleratorInvoked(object sender, EventArgs e)
    {
        if (StepButton.IsEnabled)
            OnStepButtonClicked(StepButton, EventArgs.Empty);
    }

    private void OnStopAcceleratorInvoked(object sender, EventArgs e) => OnStopButtonClicked(StopButton, EventArgs.Empty);

    private void OnPlaybackSliderValueChanged(object sender, ValueChangedEventArgs e)
    {
        if (_sliderChanging)
            return;

        var frames = Workspace.GetReconstructionFrames();
        int index = (int)Math.Round(e.NewValue);
        if (index >= 0 && index < frames.Count)
        {
            _currentFrame = frames[index];
            var results = Workspace.GetReconstructionResults();
            ReconstructionResult? res = null;
            int cumulative = 0;
            int iter = 0;
            foreach (var r in results)
            {
                cumulative += r.Frames.Count;
                iter++;
                if (index < cumulative)
                {
                    res = r;
                    break;
                }
            }
            if (res != null)
            {
                _currentResult = res;
                _viewModel.IterationCount = iter;
                _viewModel.Residual = ReconstructionStatistics.CalculateResidual(res);
            }
            Dispatcher.Dispatch(() => { InvalidateAll(); UpdatePlaybackLabel(); });

            _viewModel.SnapGradientSelectionToFrame(index);
        }
    }

    private void UpdatePlaybackLabel()
    {
        int total = (int)Math.Round(PlaybackSlider.Maximum) + 1;
        int current = (int)Math.Round(PlaybackSlider.Value) + 1;
        PlaybackFrameLabel.Text = $"{current} / {total}";
    }

    private async void OnSolveForwardClicked(object sender, EventArgs e)
    {
        if (GetDiscretization() == null)
        {
            await DisplayAlert("No Mesh", "You should create or load a mesh to start reconstrucion!", "Ok");
            return;
        }

        if (!_viewModel.CheckReconstructionMethodAgainstDiscretization())
        {
            await DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver what your mesh is made for.", "Ok");
            return;
        }

        await AnimateButtonAsync(sender);
        //_viewModel?.OnSolveForwardClicked(this, e);
    }

    private async void OnSolveInverseClicked(object sender, EventArgs e)
    {
        if (GetDiscretization() == null)
        {
            await DisplayAlert("No Mesh", "You should create or load a mesh to start reconstrucion!", "Ok");
            return;
        }

        if (!_viewModel.CheckReconstructionMethodAgainstDiscretization())
        {
            await DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver what your discretization is made for.", "Ok");
            return;
        }

        await AnimateButtonAsync(sender);
        //_viewModel?.OnSolveInverseClicked(this, e);
    }

    private async void OnEditBoundaryConditionsClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);
        var discretization = GetDiscretization();
        if (discretization is FEMMesh fem)
        {
            var bc = new FEMBoundaryCondition([.. fem.GetElectrodes().Cast<FEMElectrode>()]);
            var popup = new BoundaryConditionsPopup(bc);
            var result = await this.ShowPopupAsync(popup) as BoundaryCondition;
            if (result is FEMBoundaryCondition femBc)
            {
                fem.SetElectrodes([.. femBc.GetElectrodes().Cast<FEMElectrode>()]);
                InvalidateAll();
            }
        }
        else if (discretization is LBMGrid lbm)
        {
            var bc = new LBMBoundaryCondition([.. lbm.GetElectrodes().Cast<LBMElectrode>()]);
            var popup = new BoundaryConditionsPopup(bc);
            var result = await this.ShowPopupAsync(popup) as BoundaryCondition;
            if (result is LBMBoundaryCondition lbmBc)
            {
                lbm.SetElectrodes([.. lbmBc.GetElectrodes().Cast<LBMElectrode>()]);
                InvalidateAll();
            }
        }
    }

    private void OnAdjecentDrivePatternChecked(object sender, CheckedChangedEventArgs e)
    {
        if (e.Value)
            _viewModel.SetDrivePattern(DrivePattern.Adjecent);
    }

    private void OnOppositeDrivePatternChecked(object sender, CheckedChangedEventArgs e)
    {
        if (e.Value)
            _viewModel.SetDrivePattern(DrivePattern.Opposite);
    }

    private void OnPotentialModeChanged(object? sender, int index)
    {
        _potMode = (PotentialDisplayMode)index;
        PotentialDistributionCanvas.InvalidateSurface();
        AdjointDistributionCanvas.InvalidateSurface();
        PotentialColorbarCanvas.InvalidateSurface();
        AdjointColorbarCanvas.InvalidateSurface();
    }

    private void OnConductivityModeChanged(object? sender, int index)
    {
        if (index < 0)
            return;

        _conductivityMode = (ConductivityDisplayMode)index;
        InvalidateConductivityDisplays();
    }

    private async void OnResetReconstructionClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);
        bool confirm = await DisplayAlert("Reset Reconstruction", "Are you sure you want to reset all reconstruction parameters and progress?", "Yes", "No");
        if (!confirm)
            return;

        _viewModel.ResetAllToDefaults();

        // Clear current visuals to initial state
        _currentResult = null;
        _currentFrame = null;
        PlaybackSlider.Maximum = 0;
        PlaybackSlider.Value = 0;
        UpdatePlaybackLabel();
        InvalidateAll();
        UpdateExportButtonState();

        await DisplayAlert("Reset Complete", "Reconstruction parameters and progress were reset.", "OK");
    }
}
