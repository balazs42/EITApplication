using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class BoundaryConditionsPopupViewModel : BaseViewModel
    {
        [ObservableProperty]
        private BoundaryCondition boundaryCondition;

        [ObservableProperty]
        public ObservableCollection<Electrode> electrodes;

        public BoundaryConditionsPopupViewModel(FEMBoundaryCondition bc)
        {
            BoundaryCondition = bc;

            var electrodes = bc.GetElectrodes();

            Electrodes = new ObservableCollection<Electrode>(electrodes);
        }

        public BoundaryConditionsPopupViewModel(LBMBoundaryCondition bc)
        {
            BoundaryCondition = bc;

            var electrodes = bc.GetElectrodes();

            Electrodes = new ObservableCollection<Electrode>(electrodes);
        }

        public BoundaryCondition GetBoundaryCondition() => BoundaryCondition;
        public void SetBoundaryCondition(BoundaryCondition boundaryCondition) => BoundaryCondition = boundaryCondition;
    }
}
