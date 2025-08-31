using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using Microsoft.Maui.Controls;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using System.Threading.Tasks;
using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.Views;

public partial class ReconstructionPage : ContentPage
{
    private readonly ReconstructionPageViewModel _viewModel;
    public event EventHandler<int>? PotentialModeChanged;

    private ReconstructionResult? _currentResult;

    // FEM transform helpers
    private float _scale, _marginX, _marginY, _meshWidth, _meshHeight, _minX, _minY, _canvasHeight;

    // hover information per canvas
    private string[]? _hoverOriginalLines; private SKPoint? _hoverOriginalPt;
    private string[]? _hoverPotentialLines; private SKPoint? _hoverPotentialPt;
    private string[]? _hoverReconstructedLines; private SKPoint? _hoverReconstructedPt;
    private string[]? _hoverAdjointLines; private SKPoint? _hoverAdjointPt;

    private enum PotentialDisplayMode { Default, Grayscale, Inverted, Heatmap, Rainbow }
    private PotentialDisplayMode _potMode = PotentialDisplayMode.Default;

    private bool _isPaused = false;
    private bool _hasStarted = false;
    private bool _sliderChanging = false;

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

        StepButton.IsEnabled = false;
    }

    private IMesh? GetMesh()
        => (_currentResult?.Mesh as IMesh) ?? (IMesh?)Workspace.GetMesh();

    private static async Task AnimateButtonAsync(object sender)
    {
        if (sender is VisualElement ve)
        {
            await ve.ScaleTo(0.95, 50, Easing.CubicIn);
            await ve.ScaleTo(1, 50, Easing.CubicOut);
        }
    }

    #region Simulation control
    private async void OnPlayButtonClicked(object sender, EventArgs e)
    {
        if(!_viewModel.CheckReconstructionMethodAgainstMesh())
        {
            DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver what your mesh is made for.", "Ok");
            return;
        }

        await AnimateButtonAsync(sender);
        if (!_hasStarted)
        {
            _viewModel.StartBackgroundReconstruction();
            _hasStarted = true;
            _isPaused = false;
            StepButton.IsEnabled = false;
            return;
        }

        if (_isPaused)
        {
            _viewModel.ResumeReconstruction();
            _isPaused = false;
            StepButton.IsEnabled = false;
        }
        else
        {
            await DisplayAlert("Reconstruction Running", "You should either pause or stop reconstruction to start a new one.", "OK");
        }
    }

    private async void OnPauseButtonClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);
        _viewModel.PauseReconstruction();
        _isPaused = true;
        StepButton.IsEnabled = true;
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
        _hasStarted = false;
        StepButton.IsEnabled = false;
    }
    #endregion

    private void OnReconstructionUpdated(object? sender, ReconstructionResult result)
    {
        _currentResult = result;
        _sliderChanging = true;
        var results = Workspace.GetReconstructionResults();
        if (results.Count > 0)
        {
            PlaybackSlider.Maximum = results.Count - 1;
            PlaybackSlider.Value = PlaybackSlider.Maximum;
        }
        _sliderChanging = false;
        Dispatcher.Dispatch(InvalidateAll);
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

    private SKColor GetPotentialColor(double val, double min, double max)
    {
        var norm = (float)((val - min) / (max - min));
        norm = Math.Clamp(norm, 0f, 1f);
        return _potMode switch
        {
            PotentialDisplayMode.Grayscale => new SKColor((byte)(norm * 255), (byte)(norm * 255), (byte)(norm * 255)),
            PotentialDisplayMode.Inverted => new SKColor((byte)(255 - ColorForValue(val, min, max).Red),
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

    private void DrawFemConductivity(SKPaintSurfaceEventArgs e, FEMMesh mesh, ConductivityDistribution cd, string[]? lines, SKPoint? pt)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColor.Parse("#1E1E1E"));
        ComputeFemTransform(mesh, e.Info);
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
        DrawHoverInfo(canvas, e.Info, lines, pt);
    }

    private void DrawFemPotential(SKPaintSurfaceEventArgs e, FEMMesh mesh, PotentialDistribution pd, string[]? lines, SKPoint? pt)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColor.Parse("#1E1E1E"));
        ComputeFemTransform(mesh, e.Info);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };
        double minVal = pd.Potentials.Values.Min();
        double maxVal = pd.Potentials.Values.Max();
        if (Math.Abs(maxVal - minVal) < 1e-12) maxVal = minVal + 1e-12;
        foreach (var elem in mesh.GetElements().Cast<FEMElement>())
        {
            double avg = elem.Vertices.Average(v => pd.GetPotential(v.GlobalId));
            fill.Color = GetPotentialColor(avg, minVal, maxVal);
            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
        }
        DrawHoverInfo(canvas, e.Info, lines, pt);
    }

    private void DrawLbmField(SKPaintSurfaceEventArgs e, LBMMesh mesh, Dictionary<int, double> values, bool isPotential, string[]? lines, SKPoint? pt)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColor.Parse("#1E1E1E"));
        float cw = e.Info.Width / mesh.Nx;
        float ch = e.Info.Height / mesh.Ny;
        double minVal = values.Values.Min();
        double maxVal = values.Values.Max();
        if (Math.Abs(maxVal - minVal) < 1e-12) maxVal = minVal + 1e-12;
        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                double val = values[el.Id];
                SKColor col = el.IsWall ? SKColors.Black : isPotential ? GetPotentialColor(val, minVal, maxVal) : ColorForValue(val, minVal, maxVal);
                using var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = col };
                var r = SKRect.Create(x * cw, y * ch, cw, ch);
                canvas.DrawRect(r, paint);
                canvas.DrawRect(r, new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 });
            }
        }
        DrawHoverInfo(canvas, e.Info, lines, pt);
    }

    private void DrawColorBar(SKCanvas canvas, SKImageInfo info, double min, double max, bool isPotential)
    {
        canvas.Clear(SKColor.Parse("#1E1E1E"));
        var rect = new SKRect(0, 0, info.Width, info.Height - 20);
        int steps = 256;
        var colors = new SKColor[steps];
        var positions = new float[steps];
        for (int i = 0; i < steps; i++)
        {
            double t = 1.0 - i / (double)(steps - 1);
            double val = min + (max - min) * t;
            colors[i] = isPotential ? GetPotentialColor(val, min, max) : ColorForValue(val, min, max);
            positions[i] = i / (float)(steps - 1);
        }
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top),
                                                   new SKPoint(rect.Left, rect.Bottom),
                                                   colors,
                                                   positions,
                                                   SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, paint);
        using var border = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };
        canvas.DrawRect(rect, border);
        using var text = new SKPaint { Color = SKColors.White, TextSize = 14, IsAntialias = true };
        canvas.DrawText(max.ToString("F2"), rect.Left + 2, 14, text);
        canvas.DrawText(min.ToString("F2"), rect.Left + 2, info.Height - 2, text);
    }
    #endregion

    #region Canvas paint
    private void OnOriginalCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var mesh = GetMesh();
        if (mesh is FEMMesh fem)
        {
            var cd = _currentResult?.OriginalConductivityDistribution ?? fem.GetConductivityDistribution();
            DrawFemConductivity(e, fem, cd, _hoverOriginalLines, _hoverOriginalPt);
        }
        else if (mesh is LBMMesh lbm)
        {
            var cd = _currentResult?.OriginalConductivityDistribution ?? lbm.GetConductivityDistribution();
            DrawLbmField(e, lbm, cd.Conductivities, false, _hoverOriginalLines, _hoverOriginalPt);
        }
    }

    private void OnPotentialCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var mesh = GetMesh();
        var pd = _currentResult?.CurrentPotentialDistribution ?? mesh?.GetPotentialDistribution();
        if (mesh is FEMMesh fem && pd != null)
            DrawFemPotential(e, fem, pd, _hoverPotentialLines, _hoverPotentialPt);
        else if (mesh is LBMMesh lbm && pd != null)
            DrawLbmField(e, lbm, pd.Potentials, true, _hoverPotentialLines, _hoverPotentialPt);
    }

    private void OnReconstructedCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var mesh = GetMesh();
        var cd = _currentResult?.ReconstructedConductivityDistribution ?? mesh?.GetConductivityDistribution();
        if (mesh is FEMMesh fem && cd != null)
            DrawFemConductivity(e, fem, cd, _hoverReconstructedLines, _hoverReconstructedPt);
        else if (mesh is LBMMesh lbm && cd != null)
            DrawLbmField(e, lbm, cd.Conductivities, false, _hoverReconstructedLines, _hoverReconstructedPt);
    }

    private void OnAdjointCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var mesh = GetMesh();
        var pd = _currentResult?.CurrentAdjointDistribution ?? mesh?.GetPotentialDistribution();
        if (mesh is FEMMesh fem && pd != null)
            DrawFemPotential(e, fem, pd, _hoverAdjointLines, _hoverAdjointPt);
        else if (mesh is LBMMesh lbm && pd != null)
            DrawLbmField(e, lbm, pd.Potentials, true, _hoverAdjointLines, _hoverAdjointPt);
    }

    private void OnOriginalColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var mesh = GetMesh();
        if (mesh is FEMMesh fem)
        {
            var cd = _currentResult?.OriginalConductivityDistribution ?? fem.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
        else if (mesh is LBMMesh lbm)
        {
            var cd = _currentResult?.OriginalConductivityDistribution ?? lbm.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
    }

    private void OnPotentialColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var mesh = GetMesh();
        var pd = _currentResult?.CurrentPotentialDistribution ?? mesh?.GetPotentialDistribution();
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
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var mesh = GetMesh();
        if (mesh is FEMMesh fem)
        {
            var cd = _currentResult?.ReconstructedConductivityDistribution ?? fem.GetConductivityDistribution();
            double min = cd.Conductivities.Values.Min();
            double max = cd.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, false);
        }
        else if (mesh is LBMMesh lbm)
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
        _currentResult ??= Workspace.GetReconstructionResults().LastOrDefault();
        var mesh = GetMesh();
        var pd = _currentResult?.CurrentAdjointDistribution ?? mesh?.GetPotentialDistribution();
        if (pd != null)
        {
            double min = pd.Potentials.Values.Min();
            double max = pd.Potentials.Values.Max();
            if (Math.Abs(max - min) < 1e-12) max = min + 1e-12;
            DrawColorBar(e.Surface.Canvas, e.Info, min, max, true);
        }
    }
    #endregion

    #region Touch handlers
    private async void OnOriginalCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
        { await ShowDisplayModeMenu(); e.Handled = true; return; }
        var mesh = GetMesh();
        if (mesh == null) return;
        var view = (SKCanvasView)sender;
        if (mesh is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            {
                _hoverOriginalLines = null; _hoverOriginalPt = null; view.InvalidateSurface(); e.Handled = true; return;
            }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            _hoverOriginalLines = null; _hoverOriginalPt = null;
            var cd = (_currentResult?.OriginalConductivityDistribution ?? fem.GetConductivityDistribution());
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
        else if (mesh is LBMMesh lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx;
            float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverOriginalLines = null; _hoverOriginalPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var cd = (_currentResult?.OriginalConductivityDistribution ?? lbm.GetConductivityDistribution());
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
        var mesh = GetMesh(); if (mesh == null) return; var view = (SKCanvasView)sender;
        if (mesh is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            { _hoverPotentialLines = null; _hoverPotentialPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            var verts = fem.Vertices;
            var nearest = verts.OrderBy(v => (ToCanvas(v) - e.Location).LengthSquared).First();
            var pd = (_currentResult?.CurrentPotentialDistribution ?? fem.GetPotentialDistribution());
            double val = pd.GetPotential(nearest.GlobalId);
            _hoverPotentialLines = new[] { $"GID: {nearest.GlobalId}", $"Φ: {val:F3}" };
            _hoverPotentialPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
        else if (mesh is LBMMesh lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx;
            float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverPotentialLines = null; _hoverPotentialPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var pd = (_currentResult?.CurrentPotentialDistribution ?? lbm.GetPotentialDistribution());
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
        var mesh = GetMesh(); if (mesh == null) return; var view = (SKCanvasView)sender;
        if (mesh is FEMMesh fem)
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
        else if (mesh is LBMMesh lbm)
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
        var mesh = GetMesh(); if (mesh == null) return; var view = (SKCanvasView)sender;
        if (mesh is FEMMesh fem)
        {
            if (e.ActionType == SKTouchAction.Released)
            { _hoverAdjointLines = null; _hoverAdjointPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
            var verts = fem.Vertices;
            var nearest = verts.OrderBy(v => (ToCanvas(v) - e.Location).LengthSquared).First();
            var pd = (_currentResult?.CurrentAdjointDistribution ?? fem.GetPotentialDistribution());
            double val = pd.GetPotential(nearest.GlobalId);
            _hoverAdjointLines = new[] { $"GID: {nearest.GlobalId}", $"Φ: {val:F3}" };
            _hoverAdjointPt = e.Location;
            view.InvalidateSurface(); e.Handled = true;
        }
        else if (mesh is LBMMesh lbm)
        {
            float cw = view.CanvasSize.Width / lbm.Nx; float ch = view.CanvasSize.Height / lbm.Ny;
            int col = (int)(e.Location.X / cw); int row = (int)(e.Location.Y / ch);
            col = Math.Clamp(col, 0, lbm.Nx - 1); row = Math.Clamp(row, 0, lbm.Ny - 1);
            if (e.ActionType == SKTouchAction.Released)
            { _hoverAdjointLines = null; _hoverAdjointPt = null; view.InvalidateSurface(); e.Handled = true; return; }
            var el = lbm.GetElementAt(col, row);
            var pd = (_currentResult?.CurrentAdjointDistribution ?? lbm.GetPotentialDistribution());
            double val = pd.Potentials[el.Id];
            _hoverAdjointLines = new[] { $"ID: {el.Id}", $"Φ: {val:F3}" };
            _hoverAdjointPt = e.Location;
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
        PotentialDistributionCanvas.InvalidateSurface();
        ReconstructedDistributionCanvas.InvalidateSurface();
        AdjointDistributionCanvas.InvalidateSurface();
        OriginalColorbarCanvas.InvalidateSurface();
        PotentialColorbarCanvas.InvalidateSurface();
        ReconstructedColorbarCanvas.InvalidateSurface();
        AdjointColorbarCanvas.InvalidateSurface();
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

    private void OnPlaybackSliderValueChanged(object sender, ValueChangedEventArgs e)
    {
        if (_sliderChanging)
            return;

        var results = Workspace.GetReconstructionResults();
        int index = (int)Math.Round(e.NewValue);
        if (index >= 0 && index < results.Count)
        {
            var result = results[index];
            _currentResult = result;
            _viewModel.IterationCount = index + 1;
            _viewModel.Residual = CalculateResidual(result.ReconstructedConductivityDistribution,
                                                   result.OriginalConductivityDistribution);
            Dispatcher.Dispatch(InvalidateAll);
        }
    }

    private async void OnSolveForwardClicked(object sender, EventArgs e)
    {
        if (!_viewModel.CheckReconstructionMethodAgainstMesh())
        {
            DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver what your mesh is made for.", "Ok");
            return;
        }

        await AnimateButtonAsync(sender);
        _viewModel?.OnSolveForwardClicked(this, e);
    }

    private async void OnSolveInverseClicked(object sender, EventArgs e)
    {
        if (!_viewModel.CheckReconstructionMethodAgainstMesh())
        {
            DisplayAlert("Bad Differential Equation Solver", "You should select the same type of DE solver what your mesh is made for.", "Ok");
            return;
        }

        await AnimateButtonAsync(sender);
        _viewModel?.OnSolveInverseClicked(this, e);
    }

    private async void OnEditBoundaryConditionsClicked(object sender, EventArgs e)
    {
        await AnimateButtonAsync(sender);
        var mesh = GetMesh();
        if (mesh is FEMMesh fem)
        {
            var bc = new FEMBoundaryCondition(fem.GetElectrodes().Cast<FEMElectrode>().ToList());
            var popup = new BoundaryConditionsPopup(bc);
            await this.ShowPopupAsync(popup);
        }
        else if (mesh is LBMMesh lbm)
        {
            var bc = new LBMBoundaryCondition(lbm.GetElectrodes().Cast<LBMElectrode>().ToList());
            var popup = new BoundaryConditionsPopup(bc);
            await this.ShowPopupAsync(popup);
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