using BH.Engine.Base;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes;
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
        private FEMMesh _reconstructedMesh;

        public FEMReconstructionPageViewModel(ReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            _mesh = GenerateMesh();
            _reconstructedMesh = GenerateMesh();
        }


        public FEMMesh GenerateMesh()
        {
            return (FEMMesh)MeshFactory.Create(MeshType.FEM, 
                                               layers: Layers,
                                               boundaryVertexCount: BoundaryNodeCount);
        }

        public FEMMesh GetMesh() => _mesh;
        public FEMMesh GetReconstructionMesh() => _reconstructedMesh;

        public FEMMesh SolveForward(FEMMesh mesh)
        {
            var retMesh = _reconstructionService.SolveFemForward(mesh);;

            _reconstructedMesh.PotentialDistribution = retMesh.PotentialDistribution;

            return retMesh;
        }

        public FEMMesh SolveInverse(FEMMesh mesh)
        {
            return _reconstructionService.SolveFemInverse(mesh);
        }
    }
}
