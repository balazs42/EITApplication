using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace ElectricalImpedanceTomography.Views;

public partial class LBMReconstructionPage : ContentPage
{
	private readonly LBMReconstructionPageViewModel _viewModel;

    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.WhiteSmoke };
    private readonly SKPaint _wallPaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black };
    private readonly SKPaint _electrodePaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };

    public LBMReconstructionPage()
	{
		InitializeComponent();

		_viewModel = Utility.Composition.Container.ResolveObject<LBMReconstructionPageViewModel>();

		BindingContext = _viewModel;

        _viewModel.GenerateLbmMesh();
	}

    private void OnCanvasViewPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        SKSurface surface = e.Surface;
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var mesh = _viewModel.GetMesh();

        if (mesh == null) 
            return;

        var info = e.Info;
        float cellWidth = (float)info.Width / mesh.Nx;
        float cellHeight = (float)info.Height / mesh.Ny;

        for (int x = 0; x < mesh.Nx; x++)
        {
            for (int y = 0; y < mesh.Ny; y++)
            {
                var element = mesh.GetElementAt(x, y);

                // Determine the fill color based on the element's state
                SKPaint currentFillPaint;
                if (element.IsElectrode)
                    currentFillPaint = _electrodePaint;
                else if (element.IsWall)
                    currentFillPaint = _wallPaint;
                else
                    currentFillPaint = _fillPaint;

                var rect = SKRect.Create(x * cellWidth, y * cellHeight, cellWidth, cellHeight);
                canvas.DrawRect(rect, currentFillPaint);
                canvas.DrawRect(rect, _strokePaint);
            }
        }
    }

    // This handler is ONLY for left-clicks (tapping) to toggle walls
    private void OnCanvasTouch(object sender, SKTouchEventArgs e)
    { 
        // We only act on the initial press of a button.
        if (e.ActionType != SKTouchAction.Pressed)
        {
            e.Handled = true;
            return;
        }

        var (col, row) = GetCellCoordinatesFromPixel(e.Location);

        if (IsWithinBounds(col, row))
        {
            switch (e.MouseButton)
            {
                case SKMouseButton.Left: // Left-click toggles walls
                    _viewModel.ToggleWallStateCommand.Execute((row - 1, col));
                    break;

                case SKMouseButton.Right: // Right-click toggles electrodes
                    _viewModel.ToggleElectrodeStateCommand.Execute((row - 1, col));
                    break;
            }

            // After any action, invalidate the canvas to force a redraw.
            canvasView.InvalidateSurface();
        }

        e.Handled = true;
    }


    private (int, int) GetCellCoordinatesFromPixel(SKPoint pixelLocation)
    {        
        var mesh = _viewModel.GetMesh();
        float cellWidth = (float)canvasView.CanvasSize.Width / mesh.Nx;
        float cellHeight = (float)canvasView.CanvasSize.Height / mesh.Ny;

        int col = (int)(pixelLocation.X / cellWidth);
        int row = (int)(pixelLocation.Y / cellHeight);

        return (col, row);
    }

    private bool IsWithinBounds(int col, int row)
    {
        if (_viewModel?.GetMesh() == null) return false;
        return col >= 0 && col < _viewModel.GetMesh().Nx && row >= 0 && row < _viewModel.GetMesh().Ny;
    }

    private void OnStartReconstruction(object sender, EventArgs e)
    {
        _viewModel.OnStartReconstructionClicked(sender, e);
    }
}