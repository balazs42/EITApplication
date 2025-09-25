using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.Extensions;
using ElectricalImpedanceTomography.Helpers;
using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using System.Linq;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using System.Collections.Specialized;
using System.IO;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Microsoft.Maui.Graphics;

using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.Views;

public partial class ReconstructionPage : ContentPage
{
    private readonly ReconstructionPageViewModel _viewModel;
    public event EventHandler<int>? PotentialModeChanged;

    private ReconstructionResult? _currentResult;
    private ReconstructionFrame? _currentFrame;

    // FEM transform helpers
    private float _scale, _marginX, _marginY, _meshWidth, _meshHeight, _minX, _minY, _canvasHeight;

    // hover information per canvas
    private string[]? _hoverOriginalLines; private SKPoint? _hoverOriginalPt;
    private string[]? _hoverPotentialLines; private SKPoint? _hoverPotentialPt;
    private string[]? _hoverReconstructedLines; private SKPoint? _hoverReconstructedPt;
    private string[]? _hoverAdjointLines; private SKPoint? _hoverAdjointPt;
    private string[]? _hoverInitialLines; private SKPoint? _hoverInitialPt;
    private string[]? _hoverGradientLines; private SKPoint? _hoverGradientPt;
    private int _lastResidualRenderCount;

    private PotentialDisplayMode _potMode = PotentialDisplayMode.Default;

    private bool _isPaused = false;
    private bool _sliderChanging = false;

    private static readonly SKColor DistributionCanvasBackgroundColor = SKColor.Parse("#1A2436");
    private static readonly SKColor ChartGradientTopColor = SKColor.Parse("#23354D");
    private static readonly SKColor ChartGradientBottomColor = SKColor.Parse("#151E2D");
    private static readonly SKColor ChartLineColor = SKColor.Parse("#3A9CED");
    private static readonly SKColor ChartAreaFillColor = new SKColor(58, 156, 237, 90);
    private static readonly SKColor ChartAxisColor = SKColor.Parse("#5B6F94");
    private static readonly SKColor ChartGridColor = new SKColor(255, 255, 255, 50);
    private static readonly SKColor ChartPointColor = SKColor.Parse("#A7D2FF");
    private static readonly SKColor ChartPointOutlineColor = SKColor.Parse("#0B1C2F");
    private static readonly SKColor ChartPrimaryTextColor = new SKColor(198, 212, 245);
    private static readonly SKColor ChartSecondaryTextColor = new SKColor(157, 170, 211);

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

        _viewModel.ReconstructionUpdated += OnReconstructionUpdated;
        _viewModel.ReconstructionFrameUpdated += OnReconstructionFrameUpdated;
        _viewModel.ResidualHistory.CollectionChanged += OnResidualHistoryChanged;

        StepButton.IsEnabled = false;
        PlayButton.IsVisible = true;
        PauseButton.IsVisible = false;
        PlayerBackButton.IsEnabled = false;
        PlayerForwardButton.IsEnabled = false;

        UpdateExportButtonState();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var (startColor, endColor) = GetBackgroundPulseColors();
        this.StartBackgroundPulse(startColor, endColor);
        _viewModel.LoadAvailableReconstructions();
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
            ? (Color.FromArgb("#1B1A13"), Color.FromArgb("#2A281E"))
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

    #region Simulation control
    private async void OnPlayButtonClicked(object sender, EventArgs e)
    {
        if(GetDiscretization() == null)
        {
            await DisplayAlert("No Mesh", "You should create or load a mesh to start reconstrucion!", "Ok");
            return;
        }

        if(!_viewModel.CheckReconstructionMethodAgainstMesh())
        {
            await DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver what your mesh is made for!", "Ok");
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

    private void OnResidualHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        bool shouldInvalidate = false;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                _lastResidualRenderCount = 0;
                shouldInvalidate = true;
                break;

            case NotifyCollectionChangedAction.Add:
                int count = _viewModel.ResidualHistory.Count;
                if (count == 0)
                {
                    _lastResidualRenderCount = 0;
                    shouldInvalidate = true;
                }
                else if (count % 10 == 0 && count != _lastResidualRenderCount)
                {
                    _lastResidualRenderCount = count;
                    shouldInvalidate = true;
                }
                break;

            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                _lastResidualRenderCount = _viewModel.ResidualHistory.Count;
                shouldInvalidate = true;
                break;
        }

        if (shouldInvalidate)
        {
            MainThread.BeginInvokeOnMainThread(() => ResidualTrendCanvas.InvalidateSurface());
        }
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

    private SKColor ColorForValue(double val, double min, double max)
    {
        double mid = (min + max) * 0.5;
        if (val >= mid)
        {
            float t = (float)((val - mid) / (max - mid));
            t = Math.Clamp(t, 0f, 1f);
            byte r = (byte)(255 * t);
            return new SKColor(r, 0, 0);
        }
        else
        {
            float t = (float)((mid - val) / (mid - min));
            t = Math.Clamp(t, 0f, 1f);
            byte b = (byte)(255 * t);
            return new SKColor(0, 0, b);
        }
    }

    private SKColor GetPotentialColor(double val, double min, double max, PotentialDisplayMode? modeOverride = null)
    {
        var mode = modeOverride ?? _potMode;
        var norm = (float)((val - min) / (max - min));
        norm = Math.Clamp(norm, 0f, 1f);
        return mode switch
        {
            PotentialDisplayMode.Grayscale => new SKColor((byte)(norm * 255), (byte)(norm * 255), (byte)(norm * 255)),
            PotentialDisplayMode.Inverted =>
                new SKColor((byte)(255 - ColorForValue(val, min, max).Red),
                            (byte)(255 - ColorForValue(val, min, max).Green),
                            (byte)(255 - ColorForValue(val, min, max).Blue)),
            PotentialDisplayMode.Heatmap => new SKColor(255, (byte)(255 * (1 - norm)), 0),
            PotentialDisplayMode.Rainbow => SKColor.FromHsv(norm * 360f, 100f, 100f),
            _ => ColorForValue(val, min, max),
        };
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

    private void DrawFemConductivity(SKCanvas canvas,
                                     SKImageInfo info,
                                     FEMMesh mesh,
                                     ConductivityDistribution cd,
                                     string[]? lines,
                                     SKPoint? pt)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);
        ComputeFemTransform(mesh, info);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };
        double minVal = cd.Conductivities.Values.Min();
        double maxVal = cd.Conductivities.Values.Max();
        if (Math.Abs(maxVal - minVal) < 1e-12) maxVal = minVal + 1e-12;
        foreach (var elem in mesh.GetElements().Cast<FEMElement>())
        {
            double val = cd.GetConductivity(elem.Id);
            fill.Color = ColorForValue(val, minVal, maxVal);
            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
        }
        DrawHoverInfo(canvas, info, lines, pt);
    }

    private void DrawFemPotential(SKCanvas canvas,
                                  SKImageInfo info,
                                  FEMMesh mesh,
                                  PotentialDistribution pd,
                                  string[]? lines,
                                  SKPoint? pt,
                                  PotentialDisplayMode? modeOverride = null)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);
        ComputeFemTransform(mesh, info);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };
        double minVal = pd.Potentials.Values.Min();
        double maxVal = pd.Potentials.Values.Max();
        if (Math.Abs(maxVal - minVal) < 1e-12) maxVal = minVal + 1e-12;
        foreach (var elem in mesh.GetElements().Cast<FEMElement>())
        {
            double avg = elem.Vertices.Average(v => pd.GetPotential(v.GlobalId));
            fill.Color = GetPotentialColor(avg, minVal, maxVal, modeOverride);
            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
        }
        DrawHoverInfo(canvas, info, lines, pt);
    }

    private void DrawLbmField(SKCanvas canvas,
                              SKImageInfo info,
                              LBMGrid mesh,
                              Dictionary<int, double> values,
                              bool isPotential,
                              string[]? lines,
                              SKPoint? pt,
                              PotentialDisplayMode? modeOverride = null)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);
        float cw = info.Width / mesh.Nx;
        float ch = info.Height / mesh.Ny;
        double minVal = values.Values.Min();
        double maxVal = values.Values.Max();
        if (Math.Abs(maxVal - minVal) < 1e-12) maxVal = minVal + 1e-12;
        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                double val = values[el.Id];
                SKColor col = el.IsWall
                    ? SKColors.Black
                    : isPotential
                        ? GetPotentialColor(val, minVal, maxVal, modeOverride)
                        : ColorForValue(val, minVal, maxVal);
                using var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = col };
                var r = SKRect.Create(x * cw, y * ch, cw, ch);
                canvas.DrawRect(r, paint);
                canvas.DrawRect(r, new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 });
            }
        }
        DrawHoverInfo(canvas, info, lines, pt);
    }

    [Obsolete]
    private void DrawColorBar(SKCanvas canvas,
                              SKImageInfo info,
                              double min,
                              double max,
                              bool isPotential,
                              PotentialDisplayMode? modeOverride = null)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);
        var rect = new SKRect(0, 0, info.Width, info.Height);
        int steps = 256;
        var colors = new SKColor[steps];
        var positions = new float[steps];
        for (int i = 0; i < steps; i++)
        {
            double t = i / (double)(steps - 1);
            double val = min + (max - min) * t;
            colors[i] = isPotential ? GetPotentialColor(val, min, max, modeOverride) : ColorForValue(val, min, max);
            positions[i] = (float)t;
        }
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Bottom),
                                                   new SKPoint(rect.Right, rect.Bottom),
                                                   colors,
                                                   positions,
                                                   SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, paint);
        using var border = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };
        canvas.DrawRect(rect, border);
        using var text = new SKPaint { Color = SKColors.White, TextSize = 14, IsAntialias = true };
        canvas.DrawText(min.ToString("F2"), rect.Left, rect.Bottom - 2, text);
        float w = text.MeasureText(max.ToString("F2"));
        canvas.DrawText(max.ToString("F2"), rect.Right - w, rect.Bottom - 2, text);
    }
    #endregion

    #region Canvas paint
    private void OnResidualTrendCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        => DrawResidualTrend(e.Surface.Canvas, e.Info, _viewModel.ResidualHistory);

    [Obsolete]
    private void DrawResidualTrend(SKCanvas canvas, SKImageInfo info, IReadOnlyList<double> history)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);

        if (history.Count == 0)
        {
            using var emptyPaint = new SKPaint
            {
                Color = ChartSecondaryTextColor,
                TextSize = 14,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText("No residual data", info.Width / 2f, info.Height / 2f, emptyPaint);
            return;
        }

        double minResidual = double.MaxValue;
        double maxResidual = double.MinValue;
        foreach (double value in history)
        {
            if (value < minResidual) minResidual = value;
            if (value > maxResidual) maxResidual = value;
        }

        if (Math.Abs(maxResidual - minResidual) < 1e-12)
            maxResidual = minResidual + 1e-12;

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

            double value = maxResidual - (maxResidual - minResidual) * t;
            canvas.DrawText(value.ToString("F3"), chartRect.Left - 10f, y + valuePaint.TextSize / 3f, valuePaint);
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
            Color = ChartLineColor,
            StrokeWidth = 3,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var areaPaint = new SKPaint
        {
            Color = ChartAreaFillColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var pointPaint = new SKPaint
        {
            Color = ChartPointColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var pointOutlinePaint = new SKPaint
        {
            Color = ChartPointOutlineColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        var linePath = new SKPath();
        var areaPath = new SKPath();
        areaPath.MoveTo(origin);

        float step = count > 1 ? chartRect.Width / (count - 1) : 0f;
        SKPoint lastPoint = origin;

        for (int i = 0; i < count; i++)
        {
            double residual = history[i];
            float x = chartRect.Left + (count > 1 ? step * i : chartRect.Width / 2f);
            double normalized = (residual - minResidual) / (maxResidual - minResidual);
            float y = chartRect.Top + chartRect.Height * (float)(1 - normalized);

            if (i == 0)
                linePath.MoveTo(x, y);
            else
                linePath.LineTo(x, y);

            areaPath.LineTo(x, y);

            canvas.DrawCircle(x, y, 4f, pointPaint);
            canvas.DrawCircle(x, y, 4f, pointOutlinePaint);

            lastPoint = new SKPoint(x, y);
        }

        areaPath.LineTo(lastPoint.X, origin.Y);
        areaPath.Close();

        canvas.DrawPath(areaPath, areaPaint);
        canvas.DrawPath(linePath, linePaint);

        canvas.DrawText("Iteration", chartRect.MidX, origin.Y + axisLabelPaint.TextSize + 26f, axisLabelPaint);

        canvas.Save();
        canvas.Translate(chartRect.Left - 44f, chartRect.MidY);
        canvas.RotateDegrees(-90);
        canvas.DrawText("Residual", 0, 0, axisLabelPaint);
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
            Color = ChartLineColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            IsAntialias = true
        };

        string lastLabel = $"Iter {count}: {history[count - 1]:F3}";
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
        if (discretization is FEMMesh fem)
        {
            var cd = Workspace.GetOriginalConductivityDistribution() ?? fem.GetConductivityDistribution();
            DrawFemConductivity(e.Surface.Canvas, e.Info, fem, cd, _hoverOriginalLines, _hoverOriginalPt);
        }
        else if (discretization is LBMGrid lbm)
        {
            var cd = Workspace.GetOriginalConductivityDistribution() ?? lbm.GetConductivityDistribution();
            DrawLbmField(e.Surface.Canvas, e.Info, lbm, cd.Conductivities, false, _hoverOriginalLines, _hoverOriginalPt);
        }
    }

    private void OnPotentialCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var pd = _currentFrame?.CalculatedPotentialDistribution ?? discretization?.GetPotentialDistribution();
        if (discretization is FEMMesh fem && pd != null)
            DrawFemPotential(e.Surface.Canvas, e.Info, fem, pd, _hoverPotentialLines, _hoverPotentialPt);
        else if (discretization is LBMGrid lbm && pd != null)
            DrawLbmField(e.Surface.Canvas, e.Info, lbm, pd.Potentials, true, _hoverPotentialLines, _hoverPotentialPt);
    }

    private void OnReconstructedCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var cd = _currentResult?.ReconstructedConductivityDistribution ?? discretization?.GetConductivityDistribution();
        if (discretization is FEMMesh fem && cd != null)
            DrawFemConductivity(e.Surface.Canvas, e.Info, fem, cd, _hoverReconstructedLines, _hoverReconstructedPt);
        else if (discretization is LBMGrid lbm && cd != null)
            DrawLbmField(e.Surface.Canvas, e.Info, lbm, cd.Conductivities, false, _hoverReconstructedLines, _hoverReconstructedPt);
    }

    private void OnAdjointCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var pd = _currentFrame?.CalculatedAdjointDistribution ?? discretization?.GetPotentialDistribution();
        if (discretization is FEMMesh fem && pd != null)
            DrawFemPotential(e.Surface.Canvas, e.Info, fem, pd, _hoverAdjointLines, _hoverAdjointPt);
        else if (discretization is LBMGrid lbm && pd != null)
            DrawLbmField(e.Surface.Canvas, e.Info, lbm, pd.Potentials, true, _hoverAdjointLines, _hoverAdjointPt);
    }

    private void OnInitialCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var cd = _currentResult?.InitialConductivitiyDistribution ?? discretization?.GetConductivityDistribution();
        if (discretization is FEMMesh fem && cd != null)
            DrawFemConductivity(e.Surface.Canvas, e.Info, fem, cd, _hoverInitialLines, _hoverInitialPt);
        else if (discretization is LBMGrid lbm && cd != null)
            DrawLbmField(e.Surface.Canvas, e.Info, lbm, cd.Conductivities, false, _hoverInitialLines, _hoverInitialPt);
    }

    private void OnGradientCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var cd = _currentFrame?.ConductivityGradient;
        if (discretization is FEMMesh fem && cd != null)
            DrawFemConductivity(e.Surface.Canvas, e.Info, fem, cd, _hoverGradientLines, _hoverGradientPt);
        else if (discretization is LBMGrid lbm && cd != null)
            DrawLbmField(e.Surface.Canvas, e.Info, lbm, cd.Conductivities, false, _hoverGradientLines, _hoverGradientPt);
    }

    private void OnOriginalColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var discretization = GetDiscretization();
        if (discretization is FEMMesh fem)
        {
            var cd = _currentResult?.OriginalConductivityDistribution ?? Workspace.GetOriginalConductivityDistribution() ?? fem.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
        else if (discretization is LBMGrid lbm)
        {
            var cd = _currentResult?.OriginalConductivityDistribution ?? Workspace.GetOriginalConductivityDistribution() ?? lbm.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
    }

    private void OnPotentialColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var pd = _currentFrame?.CalculatedPotentialDistribution ?? discretization?.GetPotentialDistribution();
        if (pd != null)
        {
            double min = pd.Potentials.Values.Min();
            double max = pd.Potentials.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, true);
        }
    }

    private void OnReconstructedColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        if (discretization is FEMMesh fem)
        {
            var cd = _currentResult?.ReconstructedConductivityDistribution ?? fem.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
        else if (discretization is LBMGrid lbm)
        {
            var cd = _currentResult?.ReconstructedConductivityDistribution ?? lbm.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
    }

    private void OnAdjointColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        var pd = _currentFrame?.CalculatedAdjointDistribution ?? discretization?.GetPotentialDistribution();
        if (pd != null)
        {
            double min = pd.Potentials.Values.Min();
            double max = pd.Potentials.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, true);
        }
    }

    private void OnInitialColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var discretization = GetDiscretization();
        if (discretization is FEMMesh fem)
        {
            var cd = _currentResult?.InitialConductivitiyDistribution ?? fem.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
        else if (discretization is LBMGrid lbm)
        {
            var cd = _currentResult?.InitialConductivitiyDistribution ?? lbm.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
    }

    private void OnGradientColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var cd = _currentFrame?.ConductivityGradient;
        if (cd != null)
        {
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
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

    private static double CalculateResidual(ConductivityDistribution reconstructed, ConductivityDistribution original)
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

    private async void OnExportVideoClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);

        ExportVideoButton.IsEnabled = false;

        var popup = new VideoExportProgressPopup(_viewModel,
            PotentialDistributionCanvas.CanvasSize,
            PotentialColorbarCanvas.CanvasSize,
            ResidualTrendCanvas.CanvasSize,
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
                _viewModel.Residual = CalculateResidual(_currentResult.ReconstructedConductivityDistribution,
                                                       _currentResult.OriginalConductivityDistribution);
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
                _viewModel.Residual = CalculateResidual(res.ReconstructedConductivityDistribution,
                                                       res.OriginalConductivityDistribution);
            }
            Dispatcher.Dispatch(() => { InvalidateAll(); UpdatePlaybackLabel(); });
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

        if (!_viewModel.CheckReconstructionMethodAgainstMesh())
        {
            await DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver what your mesh is made for.", "Ok");
            return;
        }

        await AnimateButtonAsync(sender);
        _viewModel?.OnSolveForwardClicked(this, e);
    }

    private async void OnSolveInverseClicked(object sender, EventArgs e)
    {
        if (GetDiscretization() == null)
        {
            await DisplayAlert("No Mesh", "You should create or load a mesh to start reconstrucion!", "Ok");
            return;
        }

        if (!_viewModel.CheckReconstructionMethodAgainstMesh())
        {
            await DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver what your mesh is made for.", "Ok");
            return;
        }

        await AnimateButtonAsync(sender);
        _viewModel?.OnSolveInverseClicked(this, e);
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
            _viewModel.OppositeDrivePattern = false;
    }

    private void OnOppositeDrivePatternChecked(object sender, CheckedChangedEventArgs e)
    {
        if (e.Value)
            _viewModel.AdjecentDrivePattern = false;
    }

    private void OnPotentialModeChanged(object? sender, int index)
    {
        _potMode = (PotentialDisplayMode)index;
        PotentialDistributionCanvas.InvalidateSurface();
        AdjointDistributionCanvas.InvalidateSurface();
        PotentialColorbarCanvas.InvalidateSurface();
        AdjointColorbarCanvas.InvalidateSurface();
    }
}