using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes.Factories;
using Utility.Classes.Meshing;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class FEMReconstructionPageViewModel : BaseViewModel
    {
        private readonly ReconstructionService _reconstructionService;
        
        [ObservableProperty]
        private int layers = 2;

        [ObservableProperty]
        private int boundaryNodeCount = 16;

        private FEMMesh _mesh;

        public FEMReconstructionPageViewModel(ReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            _mesh = new FEMMesh();
        }


        public FEMMesh GenerateMesh()
        {
            _mesh = (FEMMesh)MeshFactory.Create(MeshType.FEM, 
                                                layers: Layers,
                                                boundaryVertexCount: BoundaryNodeCount);

            return _mesh;
        }

        public FEMMesh SolveForward()
        {
            _reconstructionService.SolveFemForward(_mesh);

            return _mesh;
        }

        public FEMMesh SolveInverse()
        {
            return _reconstructionService.SolveFemInverse(_mesh);
        }
    }
}
