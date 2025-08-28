using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using System.Threading;
using System.Threading.Tasks;

namespace ElectricalImpedanceTomography.Views;

public partial class FEMReconstructionPage : ContentPage
{
    private readonly FEMReconstructionPageViewModel _viewModel;
    private FEMMesh _mesh, _reconstructedMesh;

    private float _scale, _marginX, _marginY;
    private float _meshWidth, _meshHeight;
    private float _minX, _minY;

    // data ranges:
    private double _minPot, _maxPot;
    private double _minCond, _maxCond;
    private double _minRecon, _maxRecon;

    // hover state for potential:
    private SKPoint? _hoverPotCanvasPt;

    // hover state for potential FEMVertex
    private FEMVertex? _hoverPotFEMVertex;

    // hover state for conductivity element
    private FEMElement? _hoverCondElem;
    private SKPoint? _hoverCondCanvasPt;

    readonly SKColor ElectrodeFill = SKColors.Yellow;
    readonly SKColor ElectrodeStroke = SKColors.Black;
    const float ElectrodeRadius = 6f;

    private bool _isSimulationRunning = false;
    private bool _isPaused = false;
    private Task? _simulationTask;
    private CancellationTokenSource? _simulationCts;

    // Defining the modes
    private enum PotentialDisplayMode
    {
        Default,
        Grayscale,
        Inverted,
        Heatmap,
        Rainbow
    }

    // Track the current mode
    private PotentialDisplayMode _potMode = PotentialDisplayMode.Default;


    public FEMReconstructionPage()
    {
        _viewModel = Utility.Composition.Container.ResolveObject<FEMReconstructionPageViewModel>();

        BindingContext = _viewModel;

        _mesh = _viewModel.GetMesh();
        _reconstructedMesh = _viewModel.GetReconstructionMesh();

        InitializeComponent();
    }

    #region CANVAS RELATED 

    // ---- MESH & RANGE SETUP ----
    void ComputeBoundsAndRanges(SKImageInfo info)
    {
        // leave padding around the mesh
        const float pad = 10f; // pixels to inset mesh from canvas edges
        float availW = info.Width - 2 * pad;
        float availH = info.Height - 2 * pad;

        var verts = _mesh.Vertices;
        _minX = (float)verts.Min(v => v.X);
        _minY = (float)verts.Min(v => v.Y);
        var maxX = (float)verts.Max(v => v.X);
        var maxY = (float)verts.Max(v => v.Y);

        _meshWidth = maxX - _minX;
        _meshHeight = maxY - _minY;

        _scale = Math.Min(availW / _meshWidth,
                          availH / _meshHeight);

        // center the mesh within padded area
        float usedW = _meshWidth * _scale;
        float usedH = _meshHeight * _scale;
        _marginX = pad + (availW - usedW) / 2f;
        _marginY = pad + (availH - usedH) / 2f;

        var meshElements = _mesh.GetElements();
        var reconstructionMeshElements = _reconstructedMesh.GetElements();

        // update ranges
        _minPot = verts.Min(v => v.Potential);
        _maxPot = verts.Max(v => v.Potential);
        _minCond = meshElements.Min(el => el.Conductivity);
        _maxCond = meshElements.Max(el => el.Conductivity);
        _minRecon = reconstructionMeshElements.Min(el => el.Conductivity);
        _maxRecon = reconstructionMeshElements.Max(el => el.Conductivity);
    }

    SKPoint ToCanvas(FEMVertex v)
        => new SKPoint(
              (float)(v.X - _minX) * _scale + _marginX,
              // flip Y:
              this.PotentialCanvas.CanvasSize.Height
                - ((float)(v.Y - _minY) * _scale + _marginY)
           );

    private float Dot(SKPoint a, SKPoint b)
        => a.X * b.X + a.Y * b.Y;

    // updated PointInTriangle to call our Dot:
    bool PointInTriangle(SKPoint p, SKPoint a, SKPoint b, SKPoint c,
                         out float u, out float v, out float w)
    {
        var v0 = b - a;
        var v1 = c - a;
        var v2 = p - a;

        float d00 = Dot(v0, v0);
        float d01 = Dot(v0, v1);
        float d11 = Dot(v1, v1);
        float d20 = Dot(v2, v0);
        float d21 = Dot(v2, v1);
        float denom = d00 * d11 - d01 * d01;

        v = (d11 * d20 - d01 * d21) / denom;
        w = (d00 * d21 - d01 * d20) / denom;
        u = 1 - v - w;

        return (u >= 0) && (v >= 0) && (w >= 0);
    }

    SKColor ColorForValue(double val, double min, double max)
    {
        double mid = (min + max) * 0.5;
        if (val >= mid)
        {
            // map [mid,max] → [0,255] red channel
            float t = (float)((val - mid) / (max - mid));
            t = Math.Clamp(t, 0f, 1f);
            byte r = (byte)(255 * t);
            return new SKColor(r, 0, 0); // black at mid (t=0), red at max (t=1)
        }
        else
        {
            // map [min,mid] → [255,0] blue channel
            float t = (float)((mid - val) / (mid - min));
            t = Math.Clamp(t, 0f, 1f);
            byte b = (byte)(255 * t);
            return new SKColor(0, 0, b); // blue at min (t=1), black at mid (t=0)
        }
    }

    private SKColor GetPotentialColor(double val)
    {
        var norm = (float)((val - _minPot) / (_maxPot - _minPot));
        norm = Math.Clamp(norm, 0f, 1f);
        return _potMode switch
        {
            PotentialDisplayMode.Grayscale => new SKColor((byte)(norm * 255), (byte)(norm * 255), (byte)(norm * 255)),
            PotentialDisplayMode.Inverted => new SKColor((byte)(255 - ColorForValue(val, _minPot, _maxPot).Red), (byte)(255 - ColorForValue(val, _minPot, _maxPot).Green), (byte)(255 - ColorForValue(val, _minPot, _maxPot).Blue)),
            PotentialDisplayMode.Heatmap => new SKColor(255, (byte)(255*(1-norm)), 0), // yellow (t=0) → red (t=1)
            PotentialDisplayMode.Rainbow => SKColor.FromHsv(norm*360f, 100f, 100f), // full hue wheel
            _ => ColorForValue(val, _minPot, _maxPot),
        };
    }

    private void DrawElectrodes(SKCanvas canvas)
    {
        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = ElectrodeFill,
            IsAntialias = true
        };
        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = ElectrodeStroke,
            StrokeWidth = 1,
            IsAntialias = true
        };

        foreach (var v in _mesh.Vertices.Where(v => v.IsElectrode))
        {
            var p = ToCanvas(v);
            canvas.DrawCircle(p, ElectrodeRadius, fillPaint);
            canvas.DrawCircle(p, ElectrodeRadius, strokePaint);
        }
    }

    private void OnPotentialPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColor.Parse("#1E1E1E"));
        ComputeBoundsAndRanges(e.Info);

        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };
        var elements = _mesh.GetElements().Cast<FEMElement>().ToList();

        // draw mesh elements
        foreach (var elem in elements)
        {
            // average potential
            var avg = elem.Vertices.Average(v => v.Potential);

            // paint with the selected mode
            fill.Color = GetPotentialColor(avg);

            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();

            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
        }

        DrawElectrodes(canvas);

        // draw hover info
        if (_hoverPotFEMVertex != null && _hoverPotCanvasPt.HasValue)
        {
            var pt = _hoverPotCanvasPt.Value;
            var v = _hoverPotFEMVertex;

            // info lines
            var lines = new[]
            {
                    $"GID: {v.GlobalId}",
                    $"BID: {v.BoundaryId}",
                    $"EID: {v.ElectrodeId}",
                    $"Φ:  {v.Potential:F3}"
                };

            // measure text
            using var textPaint = new SKPaint { IsAntialias = true, Color = SKColors.White};
            using var textFont = new SKFont(SKTypeface.Default, 16);
            float boxW = lines.Max(l => textFont.MeasureText(l)) + 8;
            float boxH = lines.Length * (textFont.Size + 4) + 4;

            // choose side relative to center
            var origin = new SKPoint(e.Info.Width * .5f, e.Info.Height * .5f);
            var dir = new SKPoint(origin.X - pt.X, origin.Y - pt.Y);
            const float off = 10f;
            float x = dir.X > 0 ? pt.X + off : pt.X - off - boxW;
            float y = dir.Y > 0 ? pt.Y + off : pt.Y - off - boxH;
            var box = new SKRect(x, y, x + boxW, y + boxH);

            // background
            using var bg = new SKPaint { Color = SKColors.Gray, IsAntialias = true };
            canvas.DrawRoundRect(box, 4, 4, bg);

            // draw text
            float ty = box.Top + textFont.Size + 2;
            foreach (var line in lines)
            {
                canvas.DrawText(line, box.Left + 4, ty,
                    SKTextAlign.Left, textFont, textPaint);
                ty += textFont.Size + 4;
            }
        }
    }


    private void OnPotentialCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.ActionType == SKTouchAction.Pressed ||
            e.ActionType == SKTouchAction.Moved)
        {
            var p = e.Location;
            // find nearest FEMVertex
            _hoverPotFEMVertex = _mesh.Vertices
                .OrderBy(v => (ToCanvas(v) - p).LengthSquared)
                .First();
            _hoverPotCanvasPt = p;
            ((SKCanvasView)sender).InvalidateSurface();
            e.Handled = true;
        }
        else if (e.ActionType == SKTouchAction.Released)
        {
            _hoverPotFEMVertex = null;
            _hoverPotCanvasPt = null;
            ((SKCanvasView)sender).InvalidateSurface();
            e.Handled = true;
        }
    }

    // ===== CONDUCTIVITY (and RECONSTRUCTION) =====
    private void OnConductivityPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        => DrawConductivityMesh(e, _mesh, ConductivityCanvas);

    private void OnReconstructionPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        => DrawConductivityMesh(e, _reconstructedMesh, ReconstructionCanvas);

    void DrawConductivityMesh(SKPaintSurfaceEventArgs e, FEMMesh mesh, SKCanvasView canvasView)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColor.Parse("#1E1E1E"));
        ComputeBoundsAndRanges(e.Info);

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };

        double minVal = ReferenceEquals(mesh, _mesh)
            ? _minCond
            : _minRecon;
        double maxVal = ReferenceEquals(mesh, _mesh)
            ? _maxCond
            : _maxRecon;

        var elements = mesh.GetElements().Cast<FEMElement>();

        // fill
        foreach (var elem in elements)
        {
            fill.Color = ColorForValue(elem.Conductivity, minVal, maxVal);
            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, fill);
        }
        // outline
        foreach (var elem in elements)
        {
            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, stroke);
        }

        // hover‐info box for conductivity element
        if (_hoverCondElem != null && _hoverCondCanvasPt.HasValue)
        {
            var pt = _hoverCondCanvasPt.Value;
            var el = _hoverCondElem;

            // build the two lines
            string[] lines = {
                $"Elem: {el.Id}",
                $"σ:    {el.Conductivity:F3}"
            };

            // measure via SKFont
            using var textPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
            using var font = new SKFont(SKTypeface.Default, 16);
            float boxW = lines.Max(l => font.MeasureText(l)) + 8;
            float boxH = lines.Length * (font.Size + 4) + 4;

            // decide which side (canvas center)
            var origin = new SKPoint(e.Info.Width * .5f, e.Info.Height * .5f);
            var dir = new SKPoint(origin.X - pt.X, origin.Y - pt.Y);
            const float off = 10f;
            float x = dir.X > 0 ? pt.X + off : pt.X - off - boxW;
            float y = dir.Y > 0 ? pt.Y + off : pt.Y - off - boxH;
            var box = new SKRect(x, y, x + boxW, y + boxH);

            // semi‐opaque background
            using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 200), IsAntialias = true };
            canvas.DrawRoundRect(box, 4, 4, bg);

            // draw each line
            float ty = box.Top + font.Size + 2;
            foreach (var line in lines)
            {
                canvas.DrawText(line, box.Left + 4, ty,
                                SKTextAlign.Left, font, textPaint);
                ty += font.Size + 4;
            }
        }

        DrawElectrodes(canvas);
    }

    private void OnConductivityCanvasTouch(object sender, SKTouchEventArgs e)
        => HandleConductivityTouch(e, _mesh, ConductivityCanvas);

    private void OnReconstructionTouch(object sender, SKTouchEventArgs e)
        => HandleConductivityTouch(e, _reconstructedMesh, ReconstructionCanvas);

    private void OnPotentialModeChanged(object sender, int selectedIndex)
    {
        _potMode = (PotentialDisplayMode)selectedIndex;
        PotentialCanvas.InvalidateSurface();
        PotentialColorbar.InvalidateSurface();
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

    private async void StepButtonClicked(object sender, EventArgs e)
    {
        await Task.Run(() => _viewModel.InverseSolveStep());
        Dispatcher.Dispatch(() =>
        {
            PotentialCanvas.InvalidateSurface();
            ConductivityCanvas.InvalidateSurface();
            ReconstructionCanvas.InvalidateSurface();
        });
    }

    private async Task RunSimulationLoop(CancellationToken token)
    {
        _isSimulationRunning = true;
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
                PotentialCanvas.InvalidateSurface();
                ConductivityCanvas.InvalidateSurface();
                ReconstructionCanvas.InvalidateSurface();
            });
        }

        _isSimulationRunning = false;
    }

    private async void OnEditBoundaryConditions(object sender, EventArgs e)
    {
        // Build a boundary condition from the current mesh or use the one already set
        var electrodes = _viewModel.GetMesh().GetElectrodes().Cast<FEMElectrode>().ToList();
        var bc = _viewModel.BoundaryCondition ?? new FEMBoundaryCondition(electrodes);

        var popup = new BoundaryConditionsPopup(bc);
        var result = await this.ShowPopupAsync(popup) as BoundaryCondition;
        if (result is FEMBoundaryCondition femBc)
        {
            _viewModel.ApplyBoundaryCondition(femBc);
            // refresh canvases to reflect any electrode role changes
            PotentialCanvas.InvalidateSurface();
            ConductivityCanvas.InvalidateSurface();
        }
    }

    void HandleConductivityTouch(SKTouchEventArgs e, FEMMesh mesh, SKCanvasView cv)
    {
        if (e.ActionType == SKTouchAction.Pressed || e.ActionType == SKTouchAction.Moved)
        {
            var p = e.Location;
            _hoverCondElem = null;
            _hoverCondCanvasPt = null;

            var elements = mesh.GetElements().Cast<FEMElement>();

            foreach (var elem in elements)
            {
                var c0 = ToCanvas(elem.Vertices[0]);
                var c1 = ToCanvas(elem.Vertices[1]);
                var c2 = ToCanvas(elem.Vertices[2]);
                if (PointInTriangle(p, c0, c1, c2, out _, out _, out _))
                {
                    _hoverCondElem = elem;
                    _hoverCondCanvasPt = p;
                    break;
                }
            }

            cv.InvalidateSurface();
            e.Handled = true;
        }
        else if (e.ActionType == SKTouchAction.Released)
        {
            _hoverCondElem = null;
            _hoverCondCanvasPt = null;
            cv.InvalidateSurface();
            e.Handled = true;
        }
    }

    private void OnPotentialColorbarPaintSurface(object s, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var info = e.Info;
        float h = info.Height * .6f;
        float y = (info.Height - h) / 2f;
        var rect = new SKRect(0, y, info.Width, y + h);

        SKColor[] colors;
        float[] positions;

        switch (_potMode)
        {
            case PotentialDisplayMode.Grayscale:
                colors = new[] { SKColors.Black, SKColors.White };
                positions = new[] { 0f, 1f };
                break;

            case PotentialDisplayMode.Inverted:
                // invert the diverging: blue↔black↔red → cyan↔white↔green
                var c0 = ColorForValue(_minPot, _minPot, _maxPot);
                var c1 = ColorForValue((_minPot + _maxPot) / 2, _minPot, _maxPot);
                var c2 = ColorForValue(_maxPot, _minPot, _maxPot);
                colors = new[]{ new SKColor((byte)(255-c0.Red),(byte)(255-c0.Green),(byte)(255-c0.Blue)),
                                       new SKColor((byte)(255-c1.Red),(byte)(255-c1.Green),(byte)(255-c1.Blue)),
                                       new SKColor((byte)(255-c2.Red),(byte)(255-c2.Green),(byte)(255-c2.Blue)) };
                positions = new[] { 0f, 0.5f, 1f };
                break;

            case PotentialDisplayMode.Heatmap:
                colors = new[] { SKColors.Yellow, SKColors.Red };
                positions = new[] { 0f, 1f };
                break;

            case PotentialDisplayMode.Rainbow:
                colors = Enumerable.Range(0, 7)
                            .Select(i => SKColor.FromHsv(i * 60, 100, 100))
                            .ToArray();
                positions = new[] { 0f, 1f / 6f, 2f / 6f, 3f / 6f, 4f / 6f, 5f / 6f, 1f };
                break;

            default:
                colors = new[]{ ColorForValue(_minPot,_minPot,_maxPot),
                                       SKColors.Black,
                                       ColorForValue(_maxPot,_minPot,_maxPot) };
                positions = new[] { 0f, 0.5f, 1f };
                break;
        }

        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Right, rect.Top),
                colors, positions, SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, paint);

        // draw labels
        using var textPaint = new SKPaint { IsAntialias = true };
        using var textFont = new SKFont(SKTypeface.Default, 16);
        var minTxt = _minPot.ToString("F2");
        var midTxt = ((_minPot + _maxPot) / 2).ToString("F2");
        var maxTxt = _maxPot.ToString("F2");
        float ty = rect.Top + textFont.Size + 2;
        canvas.DrawText(minTxt, rect.Left + 2, ty, SKTextAlign.Left, textFont, textPaint);
        float wMid = textFont.MeasureText(midTxt);
        canvas.DrawText(midTxt, rect.MidX - wMid / 2, ty, SKTextAlign.Left, textFont, textPaint);
        float wMax = textFont.MeasureText(maxTxt);
        canvas.DrawText(maxTxt, rect.Right - wMax - 2, ty, SKTextAlign.Left, textFont, textPaint);
    }

    private void OnConductivityColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        => DrawColorbar(e, _minCond, _maxCond);

    private void OnReconstructionColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        => DrawColorbar(e, _minRecon, _maxRecon);

    private void DrawColorbar(SKPaintSurfaceEventArgs e, double min, double max)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var info = e.Info;
        float h = info.Height * 0.6f;
        float y = (info.Height - h) / 2f;
        var rect = new SKRect(0, y, info.Width, y + h);

        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Right, rect.Top),
                new[] {
                        ColorForValue(min, min, max),
                        SKColors.Black,
                        ColorForValue(max, min, max)
                },
                new float[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, paint);

        using var textPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var textFont = new SKFont(SKTypeface.Default, 16);
        string minTxt = min.ToString("F2");
        string midTxt = ((min + max) / 2).ToString("F2");
        string maxTxt = max.ToString("F2");

        float ty = rect.Top + textFont.Size + 2;
        canvas.DrawText(minTxt, rect.Left + 2, ty, SKTextAlign.Left, textFont, textPaint);

        float wMid = textFont.MeasureText(midTxt);
        canvas.DrawText(midTxt, rect.MidX - wMid / 2, ty, SKTextAlign.Left, textFont, textPaint);

        float wMax = textFont.MeasureText(maxTxt);
        canvas.DrawText(maxTxt, rect.Right - wMax - 2, ty, SKTextAlign.Left, textFont, textPaint);
    }

    #endregion

    // ---- BUTTON HANDLERS ----
    private void OnGenerateMeshClicked(object s, EventArgs e)
    {
        _mesh = (FEMMesh)_viewModel.GenerateMesh().DeepCopy();
        _reconstructedMesh = (FEMMesh)_viewModel.GenerateMesh().DeepCopy();
        PotentialCanvas.InvalidateSurface();
        PotentialColorbar.InvalidateSurface();
        ConductivityCanvas.InvalidateSurface();
        ConductivityColorbar.InvalidateSurface();
        ReconstructionCanvas.InvalidateSurface();
        ReconstructionColorbar.InvalidateSurface();
    }

    private async void OnSolveForwardClicked(object s, EventArgs e)
    {
        await Task.Run(() =>
        {
            _mesh = (FEMMesh)_viewModel.SolveForward(_mesh).DeepCopy();
        });
        Dispatcher.Dispatch(() =>
        {
            PotentialCanvas.InvalidateSurface();
            PotentialColorbar.InvalidateSurface();
        });
    }

    private async void OnSolveInverseClicked(object s, EventArgs e)
    {
        await Task.Run(() =>
        {
            _reconstructedMesh = (FEMMesh)_mesh.DeepCopy();
            _reconstructedMesh = (FEMMesh)_viewModel.SolveInverse(_reconstructedMesh).DeepCopy();
            var reconstrucionMeshElements = _reconstructedMesh.GetElements();
            _minRecon = reconstrucionMeshElements.Min(el => el.Conductivity);
            _maxRecon = reconstrucionMeshElements.Max(el => el.Conductivity);
        });
        Dispatcher.Dispatch(() =>
        {
            ReconstructionCanvas.InvalidateSurface();
            ReconstructionColorbar.InvalidateSurface();
        });
    }
}