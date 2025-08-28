using ElectricalImpedanceTomography.ViewModels;

namespace ElectricalImpedanceTomography.Views;

public partial class ReconstructionPage : ContentPage
{
	private readonly ReconstructionPageViewModel _viewModel;
    public event EventHandler<int>? PotentialModeChanged;

    public ReconstructionPage()
	{
		InitializeComponent();
	
		_viewModel = Utility.Composition.Container.ResolveObject<ReconstructionPageViewModel>();
	
		BindingContext = _viewModel;

        PotentialModePicker.SelectedIndexChanged += (s, e) =>
        {
            PotentialModeChanged?.Invoke(this, PotentialModePicker.SelectedIndex);
        };
    }

    private void OnPlayButtonClicked(object sender, EventArgs e)
    {

    }

    private void OnPauseButtonClicked(object sender, EventArgs e)
    {

    }

    private void OnStepButtonClicked(object sender, EventArgs e)
    {

    }

    private void OnStopButtonClicked(object sender, EventArgs e)
    {

    }

    private void OnOriginalCanvasPaintSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {

    }

    private void OnOriginalCanvasTouch(object sender, SkiaSharp.Views.Maui.SKTouchEventArgs e)
    {

    }

    private void OnPotentialCanvasPaintSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {

    }

    private void OnPotentialCanvasTouch(object sender, SkiaSharp.Views.Maui.SKTouchEventArgs e)
    {

    }

    private void OnReconstructedCanvasPaintSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {

    }

    private void OnReconstructedCanvasTouch(object sender, SkiaSharp.Views.Maui.SKTouchEventArgs e)
    {

    }

    private void OnAdjointCanvasPaintSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {

    }

    private void OnAdjointCanvasTouch(object sender, SkiaSharp.Views.Maui.SKTouchEventArgs e)
    {

    }

    private void OnSolveForwardClicked(object sender, EventArgs e)
    {
        _viewModel?.OnSolveForwardClicked(this, e);
    }

    private void OnSolveInverseClicked(object sender, EventArgs e)
    {
        _viewModel?.OnSolveInverseClicked(this, e);
    }

    private void OnEditBoundaryConditionsClicked(object sender, EventArgs e)
    {

    }

    private void OnAdjecentDrivePatternChecked(object sender, CheckedChangedEventArgs e)
    {

    }

    private void OnOppositeDrivePatternChecked(object sender, CheckedChangedEventArgs e)
    {

    }
}