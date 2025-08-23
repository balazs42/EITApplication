using CommunityToolkit.Mvvm.ComponentModel;
using Utility.Classes.Meshing;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class MeshingPageViewModel : BaseViewModel
    {
        private readonly IDAQService _daqService;


        [ObservableProperty]
        private string name;

        [ObservableProperty]
        private DateTime saveTime;

        [ObservableProperty]
        private MeshParameters meshParameters;

        [ObservableProperty]
        private double inhomogenityValue;


        private static LBMMesh _lbmMesh = MeshFactory.CreateRectangularLBMMesh(15, 15, 16);
        private static FEMMesh _femMesh = MeshFactory.CreateCircularFEMMesh(2, 16, 16);

        private IMesh _currentMesh = _femMesh;

        public MeshingPageViewModel(IDAQService dAQService)
        {
            _daqService = dAQService;
        }

        public void SaveMesh()
        {
            _daqService.SaveMesh(_currentMesh, Name);
        }

        public void LoadMesh(string name, DateTime savedAt)
        {
            _daqService.LoadMesh(name, savedAt);
        }

        public IMesh GenerateDefault(bool femMesh = true)
        {
            return MeshFactory.Create(MeshParameters, InhomogenityValue);
        }


    }
}
