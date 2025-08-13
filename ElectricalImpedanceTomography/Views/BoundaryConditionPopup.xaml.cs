using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.ViewModels;
using Utility.Classes.Measurement;

namespace ElectricalImpedanceTomography.Views
{
    public partial class BoundaryConditionsPopup : Popup
    {
        private readonly BoundaryConditionsPopupViewModel _viewModel;

        public BoundaryConditionsPopup(FEMBoundaryCondition bc)
        {
            InitializeComponent();

            _viewModel = Utility.Composition.Container.ResolveViewModelWithParam<BoundaryConditionsPopupViewModel, FEMBoundaryCondition>(bc);
            
            BindingContext = _viewModel;
        }

        public BoundaryConditionsPopup(LBMBoundaryCondition bc)
        {
            InitializeComponent();

            _viewModel = Utility.Composition.Container.ResolveViewModelWithParam<BoundaryConditionsPopupViewModel, LBMBoundaryCondition>(bc);

            BindingContext = _viewModel;
        }


        void OnCancelClicked(object sender, EventArgs e)
          => Close(null);

        void OnSaveClicked(object sender, EventArgs e)
        {
            // Apply any edits from the popup back into the boundary condition
            // instance before returning it to the caller.
            _viewModel.BoundaryCondition.Initialize(_viewModel.Electrodes);

            // return the updated BC:
            Close(_viewModel.BoundaryCondition);
        }
    }
}
