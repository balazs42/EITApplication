using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace ElectricalImpedanceTomography.Views;

public partial class LBMReconstructionPage : ContentPage
{
	private readonly LBMReconstructionPageViewModel _viewModel;

    // Paints for the mesh elements
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.WhiteSmoke };
    private readonly SKPaint _wallPaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black };
    private readonly SKPaint _electrodePaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };

    // hover state
    private LBMElement? _hoverElem;
    private SKPoint? _hoverElemCanvasPt;

    private enum HoverData { Mesh, Potential, Current, Conductivity }
    private HoverData _hoverData;
    private double _hoverValue;

    private double _potMin, _potMax;
    private double _currentMin, _currentMax;
    private double _condMin, _condMax;

    private enum PotentialDisplayMode
    {
        Default,
        Grayscale,
        Inverted,
        Heatmap,
        Rainbow
    }

    private PotentialDisplayMode _potMode = PotentialDisplayMode.Default;

    private Task? _simulationTask;
    private CancellationTokenSource? _simulationCts;
    private bool _isPaused = false;

    public LBMReconstructionPage()
	{
		InitializeComponent();

		_viewModel = Utility.Composition.Container.ResolveObject<LBMReconstructionPageViewModel>();

		BindingContext = _viewModel;

        _viewModel.GenerateLbmMesh();

        canvasView.InvalidateSurface();
        PotentialResultCanvas.InvalidateSurface();
    }

    #region Mesh Drawin Functions 

    SKColor ColorForValue(double val, double min, double max)
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

    private SKColor GetColor(double val, double min, double max)
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

    private void OnPotentialResultPaintSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        var result = _viewModel.ReconstructionResult;
        if (result?.CurrentPotentialDistribution == null)
            return;

        var mesh = (LBMMesh)result.Mesh;
        float cw = e.Info.Width / mesh.Nx;
        float ch = e.Info.Height / mesh.Ny;

        // grab potentials, compute min/max
        var pd = result.CurrentPotentialDistribution.Potentials;
        _potMin = pd.Values.Min();
        _potMax = pd.Values.Max();
        if (Math.Abs(_potMax - _potMin) < 1e-12)
            _potMax = _potMin + 1e-12;

        // draw cells
        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                var pot = pd[el.Id];

                SKPaint fill;
                if (el.IsWall)
                {
                    fill = _wallPaint;      // remain black
                }
                else
                {
                    fill = new SKPaint       // color according to mode
                    {
                        Style = SKPaintStyle.Fill,
                        Color = GetColor(pot, _potMin, _potMax)
                    };
                }

                var r = SKRect.Create(x * cw, y * ch, cw, ch);
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, _strokePaint);
            }
        }

        DrawHoverInfo(canvas, e.Info);

        PotentialColorBar.InvalidateSurface();
    }
    private void OnCanvasViewPaintSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        var mesh = _viewModel.GetMesh();
        if (mesh == null) return;

        var info = e.Info;
        float cw = (float)(info.Width / mesh.Nx);
        float ch = (float)(info.Height / mesh.Ny);

        for (int y = 0; y < mesh.Ny; y++)
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                SKPaint fill = el.IsElectrode ? _electrodePaint
                             : el.IsWall ? _wallPaint
                                              : _fillPaint;

                var r = SKRect.Create(x * cw, y * ch, cw, ch);
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, _strokePaint);
            }

        DrawHoverInfo(canvas, info);
    }

    private void DrawHoverInfo(SKCanvas canvas, SKImageInfo info)
    {
        if (_hoverElem == null || !_hoverElemCanvasPt.HasValue)
            return;

        var mesh = _viewModel.GetMesh() as LBMMesh;
        var pt = _hoverElemCanvasPt.Value;
        var el = _hoverElem;

        var lines = new List<string> { $"ID: {el.Id}" };

        switch (_hoverData)
        {
            case HoverData.Potential:
                lines.Add($"Potential: {_hoverValue:F3}");
                break;
            case HoverData.Current:
                lines.Add($"Current: {_hoverValue:F3}");
                break;
            case HoverData.Conductivity:
                lines.Add($"Conductivity: {_hoverValue:F3}");
                break;
            default:
                lines.Add($"Wall: {el.IsWall}");
                lines.Add($"Electrode: {el.IsElectrode}");
                lines.Add($"σ: {el.Conductivity:F3}");
                lines.Add($"Phi: {el.Fi.Sum()}");
                break;
        }

        using var txtPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var font = new SKFont(SKTypeface.Default, 14);
        float w = lines.Max(l => font.MeasureText(l)) + 8;
        float h = lines.Count * (font.Size + 4) + 4;

        var center = new SKPoint(info.Width / 2f, info.Height / 2f);
        var dir = new SKPoint(center.X - pt.X, center.Y - pt.Y);
        const float off = 8f;
        float bx = dir.X > 0 ? pt.X + off : pt.X - off - w;
        float by = dir.Y > 0 ? pt.Y + off : pt.Y - off - h;
        var box = new SKRect(bx, by, bx + w, by + h);

        using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 200), IsAntialias = true };
        canvas.DrawRoundRect(box, 4, 4, bg);

        float ty = box.Top + font.Size + 2;
        foreach (var line in lines)
        {
            canvas.DrawText(line, box.Left + 4, ty, SKTextAlign.Left, font, txtPaint);
            ty += font.Size + 4;
        }
    }

    // —— TOUCH / HOVER ——
    private void OnCanvasTouch(object sender, SKTouchEventArgs e)
    {
        var mesh = _viewModel.GetMesh();
        if (mesh == null) return;

        // get grid cell coords
        float cw = (float)canvasView.CanvasSize.Width / mesh.Nx;
        float ch = (float)canvasView.CanvasSize.Height / mesh.Ny;
        int col = (int)(e.Location.X / cw);
        int row = (int)(e.Location.Y / ch);

        // clamp
        if (col < 0) col = 0; if (col >= mesh.Nx) col = mesh.Nx - 1;
        if (row < 0) row = 0; if (row >= mesh.Ny) row = mesh.Ny - 1;

        if (e.ActionType == SKTouchAction.Moved || e.ActionType == SKTouchAction.Pressed)
        {
            // update hover
            _hoverElem = mesh.GetElementAt(col, row);
            _hoverElemCanvasPt = e.Location;
            _hoverData = HoverData.Mesh;
        }

        // left-click toggles walls, right-click toggles electrodes
        if (e.ActionType == SKTouchAction.Pressed)
        {
            switch (e.MouseButton)
            {
                case SKMouseButton.Left:
                    _viewModel.ToggleWallStateCommand.Execute((col, row));
                    break;
                case SKMouseButton.Right:
                    _viewModel.ToggleElectrodeStateCommand.Execute((col, row));
                    break;
            }
        }

        canvasView.InvalidateSurface();
        e.Handled = true;
    }

    private void OnPotentialTouch(object sender, SKTouchEventArgs e)
    {
        var result = _viewModel.ReconstructionResult;
        if (result?.CurrentPotentialDistribution == null)
            return;

        var mesh = (LBMMesh)result.Mesh;
        var view = sender as SKCanvasView;

        if (view == null)
            throw new NullReferenceException("View was null, check code!");

        float cw = (float)view.CanvasSize.Width / mesh.Nx;
        float ch = (float)view.CanvasSize.Height / mesh.Ny;
        int col = (int)(e.Location.X / cw);
        int row = (int)(e.Location.Y / ch);

        if (col < 0) col = 0; if (col >= mesh.Nx) col = mesh.Nx - 1;
        if (row < 0) row = 0; if (row >= mesh.Ny) row = mesh.Ny - 1;

        if (e.ActionType == SKTouchAction.Moved || e.ActionType == SKTouchAction.Pressed)
        {
            _hoverElem = mesh.GetElementAt(col, row);
            _hoverElemCanvasPt = e.Location;
            _hoverData = HoverData.Potential;
            _hoverValue = result.CurrentPotentialDistribution.Potentials[_hoverElem.Id];
        }

        PotentialResultCanvas.InvalidateSurface();
        e.Handled = true;
    }

    private void OnCurrentTouch(object sender, SKTouchEventArgs e)
    {
        var result = _viewModel.ReconstructionResult;
        if (result?.CurrentPotentialDistribution == null)
            return;

        var mesh = (LBMMesh)result.Mesh;
        var view = (SKCanvasView)sender;
        float cw = (float)view.CanvasSize.Width / mesh.Nx;
        float ch = (float)view.CanvasSize.Height / mesh.Ny;
        int col = (int)(e.Location.X / cw);
        int row = (int)(e.Location.Y / ch);

        if (col < 0) col = 0; if (col >= mesh.Nx) col = mesh.Nx - 1;
        if (row < 0) row = 0; if (row >= mesh.Ny) row = mesh.Ny - 1;

        if (e.ActionType == SKTouchAction.Moved || e.ActionType == SKTouchAction.Pressed)
        {
            _hoverElem = mesh.GetElementAt(col, row);
            _hoverElemCanvasPt = e.Location;
            _hoverData = HoverData.Current;
            _hoverValue = _hoverElem.GetCurrentAmplitude();
        }

        CurrentAmplitudeCanvas.InvalidateSurface();
        e.Handled = true;
    }

    private void OnConductivityTouch(object sender, SKTouchEventArgs e)
    {
        var result = _viewModel.ReconstructionResult;
        if (result?.Mesh == null)
            return;

        var mesh = (LBMMesh)result.Mesh;
        var view = (SKCanvasView)sender;
        float cw = (float)view.CanvasSize.Width / mesh.Nx;
        float ch = (float)view.CanvasSize.Height / mesh.Ny;
        int col = (int)(e.Location.X / cw);
        int row = (int)(e.Location.Y / ch);

        if (col < 0) col = 0; if (col >= mesh.Nx) col = mesh.Nx - 1;
        if (row < 0) row = 0; if (row >= mesh.Ny) row = mesh.Ny - 1;

        if (e.ActionType == SKTouchAction.Moved || e.ActionType == SKTouchAction.Pressed)
        {
            _hoverElem = mesh.GetElementAt(col, row);
            _hoverElemCanvasPt = e.Location;
            _hoverData = HoverData.Conductivity;
            _hoverValue = _hoverElem.Conductivity;
        }

        ConductivityCanvas.InvalidateSurface();
        e.Handled = true;
    }

    private void OnPaintCurrentAmplitudeSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        var result = _viewModel.ReconstructionResult;
        if (result?.CurrentPotentialDistribution == null)
            return;

        var mesh = (LBMMesh)_viewModel.ReconstructionResult.Mesh.DeepCopy();
        float cw = e.Info.Width / mesh.Nx;
        float ch = e.Info.Height / mesh.Ny;

        // compute current amplitudes for each element without mutating the
        // potential distribution (which would wipe the potential display)
        var elements = mesh.GetElements().Cast<LBMElement>().ToList();

        var amplitudes = new Dictionary<int, double>();
        foreach (var element in elements)
            amplitudes.Add(element.Id, element.GetCurrentAmplitude());

        //var amplitudes = elements.ToDictionary(el => el.Id, el => el.GetCurrentAmplitude());

        if (amplitudes.Count == 0)
            return;

        // compute min/max and reuse the existing color logic
        _currentMin = amplitudes.Values.Min();
        _currentMax = amplitudes.Values.Max();
        if (Math.Abs(_currentMax - _currentMin) < 1e-12)
            _currentMax = _currentMin + 1e-12; // avoid division by zero

        // draw cells using the selected display mode
        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                double amp = amplitudes[el.Id];

                SKPaint fill = el.IsWall
                    ? _wallPaint
                    : new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = GetColor(amp, _currentMin, _currentMax)
                    };

                var r = SKRect.Create(x * cw, y * ch, cw, ch);
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, _strokePaint);
            }
        }

        DrawHoverInfo(canvas, e.Info);

        CurrentColorBar.InvalidateSurface();
    }

    private void OnPaintConductivitySurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        var result = _viewModel.ReconstructionResult;
        if (result?.Mesh == null)
            return;

        var mesh = (LBMMesh)result.Mesh;
        float cw = e.Info.Width / mesh.Nx;
        float ch = e.Info.Height / mesh.Ny;

        var elements = mesh.GetElements().Cast<LBMElement>().ToList();
        _condMin = elements.Min(el => el.Conductivity);
        _condMax = elements.Max(el => el.Conductivity);
        if (Math.Abs(_condMax - _condMin) < 1e-12)
            _condMax = _condMin + 1e-12;

        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                double cond = el.Conductivity;

                SKPaint fill = el.IsWall
                    ? _wallPaint
                    : new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = GetColor(cond, _condMin, _condMax)
                    };

                var r = SKRect.Create(x * cw, y * ch, cw, ch);
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, _strokePaint);
            }
        }

        DrawHoverInfo(canvas, e.Info);

        ConductivityColorBar.InvalidateSurface();
    }

    private void DrawColorBar(SKCanvas canvas, SKImageInfo info, double min, double max)
    {
        var rect = new SKRect(0, 0, info.Width, info.Height);
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Bottom),
            new SKPoint(rect.Left, rect.Top),
            new[] { GetColor(min, min, max), GetColor(max, min, max) },
            null,
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(rect, paint);

        using var textPaint = new SKPaint { Color = SKColors.Black, TextSize = 14, IsAntialias = true };
        canvas.DrawText(max.ToString("F2"), rect.Left + 5, 20, textPaint);
        canvas.DrawText(min.ToString("F2"), rect.Left + 5, rect.Bottom - 5, textPaint);
    }

    private void OnPotentialColorBarPaintSurface(object sender, SKPaintSurfaceEventArgs e) =>
        DrawColorBar(e.Surface.Canvas, e.Info, _potMin, _potMax);

    private void OnCurrentColorBarPaintSurface(object sender, SKPaintSurfaceEventArgs e) =>
        DrawColorBar(e.Surface.Canvas, e.Info, _currentMin, _currentMax);

    private void OnConductivityColorBarPaintSurface(object sender, SKPaintSurfaceEventArgs e) =>
        DrawColorBar(e.Surface.Canvas, e.Info, _condMin, _condMax);
    #endregion
    
    private async void OnSolveForwardClicked(object sender, EventArgs e)
    {
        await Task.Run(() => _viewModel.OnSolveForwardClicked(sender, e));
        Dispatcher.Dispatch(() =>
        {
            PotentialResultCanvas.InvalidateSurface();
            CurrentAmplitudeCanvas.InvalidateSurface();
            ConductivityCanvas.InvalidateSurface();
        });
    }

    private async void OnSolveInverseClicked(object sender, EventArgs e)
    {
        await Task.Run(() => _viewModel.OnSolveInverseClicked(sender, e));
        Dispatcher.Dispatch(() =>
        {
            PotentialResultCanvas.InvalidateSurface();
            CurrentAmplitudeCanvas.InvalidateSurface();
            ConductivityCanvas.InvalidateSurface();
        });
    }

    private async void StepButtonClicked(object sender, EventArgs e)
    {
        await Task.Run(() => _viewModel.InverseSolveStep());
        Dispatcher.Dispatch(() =>
        {
            PotentialResultCanvas.InvalidateSurface();
            CurrentAmplitudeCanvas.InvalidateSurface();
            ConductivityCanvas.InvalidateSurface();
        });
    }

    private void StartStopButtonClicked(object sender, EventArgs e)
    {
        if (_simulationTask == null || _simulationTask.IsCompleted)
        {
            _simulationCts = new CancellationTokenSource();
            _isPaused = false;
            _simulationTask = RunSimulationLoop(_simulationCts.Token);
            StartStopButton.Text = "Pause";
        }
        else if (!_isPaused)
        {
            _isPaused = true;
            StartStopButton.Text = "Resume";
        }
        else
        {
            _isPaused = false;
            StartStopButton.Text = "Pause";
        }
    }

    private void StopButtonClicked(object sender, EventArgs e)
    {
        _simulationCts?.Cancel();
        _simulationTask = null;
        _isPaused = false;
        StartStopButton.Text = "Start";
    }

    private async Task RunSimulationLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_isPaused)
            {
                await Task.Delay(100, token);
                continue;
            }

            await Task.Run(() => _viewModel.InverseSolveStep());

            Dispatcher.Dispatch(() =>
            {
                PotentialResultCanvas.InvalidateSurface();
                CurrentAmplitudeCanvas.InvalidateSurface();
                ConductivityCanvas.InvalidateSurface();
            });
        }
    }

    private async void OnEditBoundaryConditions(object sender, EventArgs e)
    {
        // Build a boundary condition based on the current mesh or use the
        // previously edited one if available.
        var electrodes = _viewModel.GetMesh().GetElectrodes().Cast<LBMElectrode>().ToList();
        var bc = _viewModel.BoundaryCondition ?? new LBMBoundaryCondition(electrodes);

        var popup = new BoundaryConditionsPopup(bc);
        var result = await this.ShowPopupAsync(popup) as BoundaryCondition;

        if (result is LBMBoundaryCondition lbmBc)
        {
            // Apply updated boundary conditions to the view model and refresh the view
            _viewModel.ApplyBoundaryCondition(lbmBc);
            canvasView.InvalidateSurface();
        }
    }

    private void OnPotentialModeChanged(object sender, EventArgs e)
    {
        _potMode = (PotentialDisplayMode)PotentialModePicker.SelectedIndex;
        PotentialResultCanvas.InvalidateSurface();
        CurrentAmplitudeCanvas.InvalidateSurface();
        ConductivityCanvas.InvalidateSurface();
    }
}