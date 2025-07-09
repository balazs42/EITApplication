using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using Utility.Classes.Meshing;

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

    // hover state for potential:
    private SKPoint? _hoverPotCanvasPt;
    private double? _hoverPotValue;

    // hover state for conductivity:
    private SKPoint? _hoverCondCanvasPt;
    private double? _hoverCondValue;

    readonly SKColor HoverTextColor = SKColors.Lime;

    readonly SKColor ElectrodeFill = SKColors.Yellow;
    readonly SKColor ElectrodeStroke = SKColors.Black;
    const float ElectrodeRadius = 6f;


    public FEMReconstructionPage()
	{
		_viewModel = Utility.Composition.Container.ResolveObject<FEMReconstructionPageViewModel>();

		BindingContext = _viewModel;

        _mesh = _viewModel.GenerateMesh();
        _reconstructedMesh = _mesh;

        InitializeComponent();
	}
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

        // update ranges
        _minPot = verts.Min(v => v.Potential);
        _maxPot = verts.Max(v => v.Potential);
        _minCond = _mesh.Elements.Min(el => el.Conductivity);
        _maxCond = _mesh.Elements.Max(el => el.Conductivity);
    }

    SKPoint ToCanvas(Vertex v)
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

        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };

        foreach (var elem in _mesh.Elements)
        {
            double avg = elem.Vertices.Average(v => v.Potential);
            fillPaint.Color = ColorForValue(avg, _minPot, _maxPot);
            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, fillPaint);
            canvas.DrawPath(path, strokePaint);
        }

        if (_hoverPotValue.HasValue && _hoverPotCanvasPt.HasValue)
        {
            using var textPaint = new SKPaint { Color = HoverTextColor, TextSize = 24, IsAntialias = true };
            var txt = _hoverPotValue.Value.ToString("F3");
            var pt = _hoverPotCanvasPt.Value;
            canvas.DrawText(txt, pt.X + 10, pt.Y - 10, textPaint);
        }

        DrawElectrodes(canvas);
    }



    // ---- POTENTIAL COLORBAR ----
    private void OnPotentialColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
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
                new[] { ColorForValue(_minPot, _minPot, _maxPot), SKColors.White, ColorForValue(_maxPot, _minPot, _maxPot) },
                new float[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, paint);

        using var txt = new SKPaint { IsAntialias = true, TextSize = 20, Color = SKColors.White, FakeBoldText = true };
        string minTxt = _minPot.ToString("F2");
        string midTxt = ((_minPot + _maxPot) * 0.5).ToString("F2");
        string maxTxt = _maxPot.ToString("F2");
        float textY = rect.Top + txt.TextSize + 0f;
        canvas.DrawText(minTxt, rect.Left + 2f, textY, txt);
        float wMid = txt.MeasureText(midTxt);
        canvas.DrawText(midTxt, rect.MidX - wMid / 2f, textY, txt);
        float w = txt.MeasureText(maxTxt);
        canvas.DrawText(maxTxt, rect.Right - w - 2f, textY, txt);
    }

    private void OnPotentialCanvasTouch(object sender, SKTouchEventArgs e)
    {
        if (e.ActionType == SKTouchAction.Pressed || e.ActionType == SKTouchAction.Moved)
        {
            var p = e.Location;
            _hoverPotValue = null;
            _hoverPotCanvasPt = null;
            foreach (var elem in _mesh.Elements)
            {
                var c0 = ToCanvas(elem.Vertices[0]);
                var c1 = ToCanvas(elem.Vertices[1]);
                var c2 = ToCanvas(elem.Vertices[2]);
                if (PointInTriangle(p, c0, c1, c2, out var u, out var v, out var w))
                {
                    _hoverPotValue = u * elem.Vertices[0].Potential +
                                     v * elem.Vertices[1].Potential +
                                     w * elem.Vertices[2].Potential;
                    _hoverPotCanvasPt = p;
                    break;
                }
            }
            PotentialCanvas.InvalidateSurface();
            e.Handled = true;
        }
        else if (e.ActionType == SKTouchAction.Released)
        {
            _hoverPotValue = null;
            _hoverPotCanvasPt = null;
            PotentialCanvas.InvalidateSurface();
            e.Handled = true;
        }
    }

    private void OnReconstructionPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        OnConductivityPaintSurface(sender, e, false);
    }

    private void OnReconstructionColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        OnConductivityColorbarPaintSurface(sender, e, false);
    }

    private void OnReconstructionTouch(object sender, SKTouchEventArgs e)
    {
        OnConductivityCanvasTouch(sender, e, false);
    }


    // ---- DRAW CONDUCTIVITY MESH ----
    private void OnConductivityPaintSurface(object sender, SKPaintSurfaceEventArgs e, bool conductivtiyCanvas = true)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColor.Parse("#1E1E1E"));

        // Fill each element
        using var fillPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        FEMMesh mesh = conductivtiyCanvas ? _mesh : _reconstructedMesh;
        


        foreach (var elem in mesh.Elements)
        {
            fillPaint.Color = ColorForValue(
                elem.Conductivity, _minCond, _maxCond);

            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, fillPaint);
        }

        // Outline
        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Black,
            StrokeWidth = 1,
            IsAntialias = true
        };
        foreach (var elem in _mesh.Elements)
        {
            using var path = new SKPath();
            path.MoveTo(ToCanvas(elem.Vertices[0]));
            path.LineTo(ToCanvas(elem.Vertices[1]));
            path.LineTo(ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, strokePaint);
        }

        if (_hoverCondValue.HasValue && _hoverCondCanvasPt.HasValue)
        {
            using var textPaint = new SKPaint
            {
                Color = HoverTextColor,
                TextSize = 24,
                IsAntialias = true
            };
            var txt = _hoverCondValue.Value.ToString("F3");
            var pt = _hoverCondCanvasPt.Value;
            canvas.DrawText(txt, pt.X + 10, pt.Y - 10, textPaint);
        }

        DrawElectrodes(canvas);
    }

    private void OnConductivityCanvasTouch(object sender, SKTouchEventArgs e, bool conductivityCanvas = true)
    {
        if (e.ActionType == SKTouchAction.Pressed || e.ActionType == SKTouchAction.Moved)
        {
            var p = e.Location;
            _hoverCondValue = null;
            _hoverCondCanvasPt = null;

            FEMMesh mesh = conductivityCanvas ? _mesh : _reconstructedMesh;

            foreach (var elem in mesh.Elements)
            {
                var c0 = ToCanvas(elem.Vertices[0]);
                var c1 = ToCanvas(elem.Vertices[1]);
                var c2 = ToCanvas(elem.Vertices[2]);
                if (PointInTriangle(p, c0, c1, c2, out _, out _, out _))
                {
                    _hoverCondValue = elem.Conductivity;
                    _hoverCondCanvasPt = p;
                    break;
                }
            }
            ConductivityCanvas.InvalidateSurface();
            e.Handled = true;
        }
        else if (e.ActionType == SKTouchAction.Released)
        {
            _hoverCondValue = null;
            _hoverCondCanvasPt = null;
            ConductivityCanvas.InvalidateSurface();
            e.Handled = true;
        }
    }


    // ---- CONDUCTIVITY COLORBAR ----
    private void OnConductivityColorbarPaintSurface(object sender, SKPaintSurfaceEventArgs e, bool conductivityCanvas = true)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);
        var info = e.Info;
        float h = info.Height * 0.9f;
        float y = 2f;

        var rect = new SKRect(0, y, info.Width, y + h);
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Right, rect.Top),
                new[] { ColorForValue(_minCond, _minCond, _maxCond),
                            ColorForValue(_maxCond, _minCond, _maxCond) },
                null, SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, paint);

        using var txt = new SKPaint
        {
            IsAntialias = true,
            TextSize = 20,
            Color = SKColors.White,
            FakeBoldText = true
        };
        var minTxt = _minCond.ToString("F2");
        var maxTxt = _maxCond.ToString("F2");
        float textY = rect.Top + txt.TextSize + 3;
        canvas.DrawText(minTxt, rect.Left + 2, textY, txt);
        float w = txt.MeasureText(maxTxt);
        canvas.DrawText(maxTxt, rect.Right - w - 2, textY, txt);
    }


    // ---- BUTTON HANDLERS ----
    private void OnGenerateMeshClicked(object s, EventArgs e)
    {
        _mesh = _viewModel.GenerateMesh();
        PotentialCanvas.InvalidateSurface();
        PotentialColorbar.InvalidateSurface();
        ConductivityCanvas.InvalidateSurface();
        ConductivityColorbar.InvalidateSurface();
    }

    private void OnSolveForwardClicked(object s, EventArgs e)
    {
        _mesh = _viewModel.SolveForward();
        PotentialCanvas.InvalidateSurface();
        PotentialColorbar.InvalidateSurface();
    }

    private void OnSolveInverseClicked(object s, EventArgs e)
    {
        _reconstructedMesh = _viewModel.SolveInverse();
        ConductivityCanvas.InvalidateSurface();
        ConductivityColorbar.InvalidateSurface();
    }
}