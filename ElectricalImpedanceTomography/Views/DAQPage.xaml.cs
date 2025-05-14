using ElectricalImpedanceTomography.ViewModels;

namespace ElectricalImpedanceTomography.Views;

public partial class DAQPage : ContentPage
{
	private readonly DAQPageViewModel _viewModel;

	public DAQPage()
	{
		InitializeComponent();

		_viewModel = Utility.Composition.Container.ResolveObject<DAQPageViewModel>();

		BindingContext = _viewModel;
	}
}