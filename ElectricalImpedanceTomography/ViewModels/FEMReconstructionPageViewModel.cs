using BH.Engine.Base;
using BH.Engine.Diffing;
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

        [ObservableProperty]
        private int electrodeCount = 16;

        [ObservableProperty]
        private int excitationElectrodeId = 1;

        [ObservableProperty]
        private int groundElectrodeId = 16;

        [ObservableProperty]
        private double excitationCurrentAmplitude = 1.0;

        [ObservableProperty]
        private double electrodeSurfaceLength = 0.3;

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
            if (BoundaryNodeCount < ElectrodeCount)
                ElectrodeCount = BoundaryNodeCount;

            return (FEMMesh)MeshFactory.Create(MeshType.FEM, 
                                               layers: Layers,
                                               boundaryVertexCount: BoundaryNodeCount, 
                                               electrodeCount: ElectrodeCount);
        }

        public FEMMesh GetMesh() => _mesh;
        public FEMMesh GetReconstructionMesh() => _reconstructedMesh;

        public FEMMesh SolveForward(FEMMesh mesh)
        {
            var electrodes = mesh.Electrodes;

            foreach(var el in electrodes)
            {
                el.IsExcitation = false;
                el.IsGround = false;
                el.Current = 0.0;
                el.Voltage = 0.0;
                el.ZContact = 0.1;
                el.Length = ElectrodeSurfaceLength;
            }

            if (ExcitationElectrodeId == GroundElectrodeId)
                ExcitationElectrodeId++;

            electrodes[ExcitationElectrodeId  % ElectrodeCount].IsExcitation = true;
            electrodes[ExcitationElectrodeId % ElectrodeCount].Current = ExcitationCurrentAmplitude;
            electrodes[GroundElectrodeId % ElectrodeCount].IsGround = true;
            electrodes[GroundElectrodeId % ElectrodeCount].Current = -ExcitationCurrentAmplitude;
            
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
