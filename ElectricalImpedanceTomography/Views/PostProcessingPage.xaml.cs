using ElectricalImpedanceTomography.ViewModels;

namespace ElectricalImpedanceTomography.Views
{
    public partial class PostProcessingPage : ContentPage
    {
        private readonly PostProcessingPageViewModel _viewModel;

        public PostProcessingPage()
        {
            InitializeComponent();
            _viewModel = new PostProcessingPageViewModel();
            BindingContext = _viewModel;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (!_viewModel.HasMesh)
            {
                _viewModel.LoadLatestWorkspaceResult();
            }
        }
    }
}
