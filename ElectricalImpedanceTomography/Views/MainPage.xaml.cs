using ElectricalImpedanceTomography.ViewModels;

namespace ElectricalImpedanceTomography.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainPageViewModel _viewModel;

        public MainPage()
        {
            InitializeComponent();

            _viewModel = Utility.Composition.Container.ResolveObject<MainPageViewModel>();

            BindingContext = _viewModel;
        }

        private void OnLoadMeasurementClicked(object sender, EventArgs e)
        {
            _viewModel.OnLoadMeasurementClicked(sender, e);
        }

        private void OnLoadMeshClicked(object sender, EventArgs e)
        {
            _viewModel.OnLoadMeshClicked(sender, e);
        }
    }
}
