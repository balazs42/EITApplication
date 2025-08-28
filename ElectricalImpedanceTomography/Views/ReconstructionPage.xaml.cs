using ElectricalImpedanceTomography.ViewModels;

namespace ElectricalImpedanceTomography.Views;

public partial class ReconstructionPage : ContentPage
{
	private readonly ReconstructionPageViewModel _viewModel;

	public ReconstructionPage()
	{
		InitializeComponent();
	
		_viewModel = Utility.Composition.Container.ResolveObject<ReconstructionPageViewModel>();
	
		BindingContext = _viewModel;
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
}