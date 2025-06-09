using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace ElectricalImpedanceTomography.Views;

public partial class ReconstructionPage : ContentPage
{
	private readonly ReconstructionPageViewModel _viewModel;

    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.WhiteSmoke };
    private readonly SKPaint _wallPaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black };
    private readonly SKPaint _electrodePaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange };
    private readonly SKPaint _strokePaint = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };

    public ReconstructionPage()
	{
		InitializeComponent();

		_viewModel = Utility.Composition.Container.ResolveObject<ReconstructionPageViewModel>();

		BindingContext = _viewModel;

        //InitializePickers();
	}

    private void OnCanvasViewPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        SKSurface surface = e.Surface;
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        if (_viewModel?.LbmMesh == null) return;
        var mesh = _viewModel.LbmMesh;
        var info = e.Info;
        float cellWidth = (float)info.Width / mesh.Nx;
        float cellHeight = (float)info.Height / mesh.Ny;

        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var element = mesh.GetElementAt(x, y);

                // --- UPDATED DRAWING LOGIC ---
                // Determine the fill color based on the element's state
                SKPaint currentFillPaint;
                if (element.IsElectrode)
                {
                    currentFillPaint = _electrodePaint;
                }
                else if (element.IsWall)
                {
                    currentFillPaint = _wallPaint;
                }
                else
                {
                    currentFillPaint = _fillPaint;
                }
                // --- END UPDATED LOGIC ---

                var rect = SKRect.Create(x * cellWidth, y * cellHeight, cellWidth, cellHeight);
                canvas.DrawRect(rect, currentFillPaint);
                canvas.DrawRect(rect, _strokePaint);
            }
        }
    }

    // This handler is now ONLY for left-clicks (tapping) to toggle walls
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
            // --- THIS IS THE NEW, SIMPLIFIED LOGIC ---
            switch (e.MouseButton)
            {
                case SKMouseButton.Left: // Left-click toggles walls
                    _viewModel.ToggleWallStateCommand.Execute((col, row));
                    break;

                case SKMouseButton.Right: // Right-click toggles electrodes
                    _viewModel.ToggleElectrodeStateCommand.Execute((col, row));
                    break;
            }

            // After any action, invalidate the canvas to force a redraw.
            canvasView.InvalidateSurface();
        }

        e.Handled = true;
    }


    private (int, int) GetCellCoordinatesFromPixel(SKPoint pixelLocation)
    {
        if (_viewModel?.LbmMesh == null) return (-1, -1);

        var mesh = _viewModel.LbmMesh;
        float cellWidth = (float)canvasView.CanvasSize.Width / mesh.Nx;
        float cellHeight = (float)canvasView.CanvasSize.Height / mesh.Ny;

        int col = (int)(pixelLocation.X / cellWidth);
        int row = (int)(pixelLocation.Y / cellHeight);

        return (col, row);
    }

    private bool IsWithinBounds(int col, int row)
    {
        if (_viewModel?.LbmMesh == null) return false;
        return col >= 0 && col < _viewModel.LbmMesh.Nx && row >= 0 && row < _viewModel.LbmMesh.Ny;
    }

    public void InitializePickers()
    {
        var DESolverList = new List<string>() { "Finite Element Method", "Lattice Boltzmann Method" };
        DESolverPicker.ItemsSource = DESolverList;

        var RegularaizationTechniqueList = new List<string>() { "None", "Zero-Order Tikhonov", "First-Order Tikhonov", "Total Variation", "Laplace" };
        RegulariaztionPicker.ItemsSource = RegularaizationTechniqueList;

        var ErrorMetricList = new List<string>() { "L2", "Wasserstein-2" };
        ErrorMetricPicker.ItemsSource = ErrorMetricList;

        var NumericSolverList = new List<string>() { "LU Decomposition", "SVD", "tSVD", "GMRES" };
        NumericSolverPicker.ItemsSource = NumericSolverList;

        var NumericOptimizerList = new List<string>() { "Gradient Based", "Particle Swarm" };
        NumericOptimizerPicker.ItemsSource = NumericOptimizerList;
    }

    private void OnStartReconstruction(object sender, EventArgs e)
    {
        _viewModel.OnStartReconstructionClicked(sender, e);
    }
}