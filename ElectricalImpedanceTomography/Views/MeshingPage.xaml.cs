using ElectricalImpedanceTomography.ViewModels;

namespace ElectricalImpedanceTomography.Views;

public partial class MeshingPage : ContentPage
{
	private readonly MeshingPageViewModel _viewModel;

	public MeshingPage()
	{
		InitializeComponent();

		_viewModel = Utility.Composition.Container.ResolveObject<MeshingPageViewModel>();

		BindingContext = _viewModel;
	}

	public void SaveMesh()
	{
		_viewModel.SaveMesh();
	}

	public void LoadMesh()
	{
		// TODO: correct implementation
		_viewModel.LoadMesh("", DateTime.Now);
	}

    private void OnMeshCanvasPaintSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
    {

    }

    private void OnMeshCanvasTouch(object sender, SkiaSharp.Views.Maui.SKTouchEventArgs e)
    {

    }

    private void OnSaveClicked(object sender, EventArgs e)
    {
		//TODO: Implement
		SaveMesh();
    }

    private void OnLoadClicked(object sender, EventArgs e)
    {
		// TODO: Implement
    }
}