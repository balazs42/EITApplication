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
    }
}
