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

	public void OnReconstructionParametersChanged(object sender, EventArgs e)
	{
		_viewModel.OnReconstructionParametersChanged(sender, e);
	}
}