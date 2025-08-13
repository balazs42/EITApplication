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

        // TODO: save really
        void OnSaveClicked(object sender, EventArgs e)
        {
            // push any list‐level changes back into the BoundaryCondition:
            //_viewModel.SetBoundaryCondition(_viewModel.GetBoundaryCondition().Electrodes .ToList());

            // re‐initialize ground/excitation indices:
            //_viewModel.BoundaryCondition.Initialize(_viewModel.BoundaryCondition.Electrodes);

            // return the updated BC:
            Close(_viewModel.BoundaryCondition);
        }
    }
}
