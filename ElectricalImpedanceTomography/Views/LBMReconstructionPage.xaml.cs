using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using Utility.Classes.Meshing;

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

    private double _maxPot, _minPot;

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

    SKColor BlueToRed(double v)
    {
        // normalize into [0,1]
        float t = (float)((v - _minPot) / (_maxPot - _minPot));
        t = Math.Clamp(t, 0f, 1f);

        // interpolate: blue=(0,0,255) → red=(255,0,0)
        byte r = (byte)(t * 255);
        byte b = (byte)((1 - t) * 255);
        return new SKColor(r, 0, b);
    }

    private void OnPotentialResultPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        // draw grid first
        OnCanvasViewPaintSurface(sender, e);

        var result = _viewModel.ReconstructionResult;
        if (result?.CurrentPotentialDistribution == null)
            return;

        var mesh = (LBMMesh)result.Mesh;
        float cw = e.Info.Width / mesh.Nx;
        float ch = e.Info.Height / mesh.Ny;

        var pd = result.CurrentPotentialDistribution.Potentials;
        double min = pd.Values.Min();
        double max = pd.Values.Max();

        _minPot = min;
        _maxPot = max;

        // simple blue→red
        SKColor BlueToRed(double v)
        {
            float t = (float)((v - min) / (max - min));
            t = Math.Clamp(t, 0f, 1f);
            byte r = (byte)(t * 255);
            byte b = (byte)((1 - t) * 255);
            return new SKColor(r, 0, b);
        }

        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                var pot = pd[el.Id];

                var fill = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = BlueToRed(pot)
                };
                var r = SKRect.Create(x * cw, y * ch, cw, ch);

                if (el.IsWall)
                    fill = _wallPaint;

                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, _strokePaint);
            }
        }
    }

    private void OnCanvasViewPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        var mesh = _viewModel.GetMesh();
        if (mesh == null) return;

        var info = e.Info;
        float cw = (float)(info.Width / mesh.Nx);
        float ch = (float)(info.Height / mesh.Ny);

        // draw all cells
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

        // —— hover‐info box ——  
        if (_hoverElem != null && _hoverElemCanvasPt.HasValue)
        {
            var pt = _hoverElemCanvasPt.Value;
            var el = _hoverElem;

            // lines to display
            string[] lines = {
                    $"ID:   {el.Id}",
                    $"Wall:      {el.IsWall}",
                    $"Electrode: {el.IsElectrode}",
                    $"σ:   {el.Conductivity:F3}",
                    $"Phi: {el.Fi.Sum()}"
                };

            // measure
            using var txtPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
            using var font = new SKFont(SKTypeface.Default, 14);
            float w = lines.Max(l => font.MeasureText(l)) + 8;
            float h = lines.Length * (font.Size + 4) + 4;

            // choose side relative to canvas center
            var center = new SKPoint(info.Width / 2f, info.Height / 2f);
            var dir = new SKPoint(center.X - pt.X, center.Y - pt.Y);
            const float off = 8f;
            float bx = dir.X > 0 ? pt.X + off : pt.X - off - w;
            float by = dir.Y > 0 ? pt.Y + off : pt.Y - off - h;
            var box = new SKRect(bx, by, bx + w, by + h);

            // background
            using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 200), IsAntialias = true };
            canvas.DrawRoundRect(box, 4, 4, bg);

            // text
            float ty = box.Top + font.Size + 2;
            foreach (var line in lines)
            {
                canvas.DrawText(line, box.Left + 4, ty, SKTextAlign.Left, font, txtPaint);
                ty += font.Size + 4;
            }
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
        }

        // left-click toggles walls, right-click toggles electrodes
        if (e.ActionType == SKTouchAction.Pressed)
        {
            switch (e.MouseButton)
            {
                case SKMouseButton.Left:
                    _viewModel.ToggleWallStateCommand.Execute((row, col));
                    break;
                case SKMouseButton.Right:
                    _viewModel.ToggleElectrodeStateCommand.Execute((row, col));
                    break;
            }
        }

        canvasView.InvalidateSurface();
        e.Handled = true;
    }
    #endregion

    private void OnStartReconstruction(object sender, EventArgs e)
    {
        _viewModel.OnStartReconstructionClicked(sender, e);
        PotentialResultCanvas.InvalidateSurface();
    }
}