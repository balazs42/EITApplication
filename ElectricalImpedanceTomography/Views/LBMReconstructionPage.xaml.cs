using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
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

    private double _maxPot, _minPot;

    private enum PotentialDisplayMode
    {
        Default,
        Grayscale,
        Inverted,
        Heatmap,
        Rainbow
    }

    private PotentialDisplayMode _potMode = PotentialDisplayMode.Default;

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

    private SKColor GetPotentialColor(double val)
    {
        var norm = (float)((val - _minPot) / (_maxPot - _minPot));
        norm = Math.Clamp(norm, 0f, 1f);
        return _potMode switch
        {
            PotentialDisplayMode.Grayscale => new SKColor((byte)(norm * 255), (byte)(norm * 255), (byte)(norm * 255)),
            PotentialDisplayMode.Inverted => new SKColor((byte)(255 - ColorForValue(val, _minPot, _maxPot).Red), (byte)(255 - ColorForValue(val, _minPot, _maxPot).Green), (byte)(255 - ColorForValue(val, _minPot, _maxPot).Blue)),
            PotentialDisplayMode.Heatmap => new SKColor(255, (byte)(255 * (1 - norm)), 0),
            PotentialDisplayMode.Rainbow => SKColor.FromHsv(norm * 360f, 100f, 100f),
            _ => ColorForValue(val, _minPot, _maxPot),
        };
    }

    private void OnPotentialResultPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        // draw the base grid
        OnCanvasViewPaintSurface(sender, e);

        var result = _viewModel.ReconstructionResult;
        if (result?.CurrentPotentialDistribution == null)
            return;

        var mesh = (LBMMesh)result.Mesh;
        float cw = e.Info.Width / mesh.Nx;
        float ch = e.Info.Height / mesh.Ny;

        // grab potentials, compute min/max
        var pd = result.CurrentPotentialDistribution.Potentials;
        _minPot = pd.Values.Min();
        _maxPot = pd.Values.Max();

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
                        Color = GetPotentialColor(pot)
                    };
                }

                var r = SKRect.Create(x * cw, y * ch, cw, ch);
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

            List<string> lines = [];

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();

            if(el.IsElectrode)
            {
                var elec = electrodes.Find(x => x.GridId == el.Id);
                if (elec == null)
                    throw new NullReferenceException("Cannot find electrode in mesh with the same id!");

                // lines to display
                lines = new List<string>() {
                        $"ID:   {el.Id}",
                        $"Wall:      {el.IsWall}",
                        $"Electrode: {el.IsElectrode}",
                        $"Excitation: { ((elec.IsExcitation || elec.IsGround == true)  ? "True" : "False")}",
                        $"σ:   {el.Conductivity:F3}",
                        $"Phi: {el.Fi.Sum()}"
                    };
            }
            else
            {
                lines = new List<string>() {
                        $"ID:   {el.Id}",
                        $"Wall:      {el.IsWall}",
                        $"Electrode: {el.IsElectrode}",
                        $"σ:   {el.Conductivity:F3}",
                        $"Phi: {el.Fi.Sum()}"
                    };
            }

            // measure
            using var txtPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
            using var font = new SKFont(SKTypeface.Default, 14);
            float w = lines.Max(l => font.MeasureText(l)) + 8;
            float h = lines.Count * (font.Size + 4) + 4;

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


    private void OnPaintCurrentAmplitudeSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        // draw the base grid
        OnCanvasViewPaintSurface(sender, e);

        var result = _viewModel.ReconstructionResult;
        if (result?.CurrentPotentialDistribution == null)
            return;

        var mesh = (LBMMesh)result.Mesh;
        float cw = e.Info.Width / mesh.Nx;
        float ch = e.Info.Height / mesh.Ny;

        // grab potentials, compute min/max
        var pd = result.CurrentPotentialDistribution.Potentials;

        var elements = mesh.GetElements().Cast<LBMElement>().ToList();

        // Convert to current amplitudes
        foreach(var p in pd)
        {
            var el = elements.Find(x => x.Id == p.Key);

            if(el != null)
                pd[p.Key] = el.GetCurrentAmplitude();
        }


        double min = pd.Values.Min();
        double max = pd.Values.Max();

        // rainbow: blue at t=0 → red at t=1
        SKColor Rainbow(double v)
        {
            float t = (float)((v - min) / (max - min));
            t = Math.Clamp(t, 0f, 1f);

            // hue: 240° (blue) → 0° (red)
            float hue = 240f * (1f - t);
            return SKColor.FromHsv(hue, 100f, 100f);
        }

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
                    fill = new SKPaint       // rainbow fill
                    {
                        Style = SKPaintStyle.Fill,
                        Color = Rainbow(pot)
                    };
                }

                var r = SKRect.Create(x * cw, y * ch, cw, ch);
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, _strokePaint);
            }
        }
    }
    #endregion
    
    private void OnSolveForwardClicked(object sender, EventArgs e)
    {
        _viewModel.OnSolveForwardClicked(sender, e);
        PotentialResultCanvas.InvalidateSurface();
        CurrentAmplitudeCanvas.InvalidateSurface();
    }

    private void OnSolveInverseClicked(object sender, EventArgs e)
    {
        _viewModel.OnSolveInverseClicked(sender, e);
    }

    private async void OnEditBoundaryConditions(object sender, EventArgs e)
    {
        // build the appropriate BoundaryCondition subclass:
        var electrodes = _viewModel.GetMesh().GetElectrodes().Cast<LBMElectrode>().ToList();

        var bc = new LBMBoundaryCondition(electrodes);

        var popup = new BoundaryConditionsPopup(bc);
        // ShowPopupAsync returns whatever you passed to Close(...)
        var result = await this.ShowPopupAsync(popup) as BoundaryCondition;
        if (result != null)
        {
            // user clicked SAVE
            // apply it to your solver / view‐model:
            //            _viewModel.BoundaryCondition = result;
            // re‐solve or redraw:
            //          _viewModel.RunForwardSolve();
        }
    }

    private void OnPotentialModeChanged(object sender, EventArgs e)
    {
        _potMode = (PotentialDisplayMode)PotentialModePicker.SelectedIndex;
        PotentialResultCanvas.InvalidateSurface();
    }
}