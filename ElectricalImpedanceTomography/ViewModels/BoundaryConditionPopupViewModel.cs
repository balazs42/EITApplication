using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing;

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

            Electrodes = new ObservableCollection<Electrode>(bc.Electrodes);
        }

        public BoundaryConditionsPopupViewModel(LBMBoundaryCondition bc)
        {
            BoundaryCondition = bc;

            Electrodes = new ObservableCollection<Electrode>(bc.GetElectrodes());
        }
    }
}
