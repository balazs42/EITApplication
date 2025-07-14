
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes.Factories;
using Utility.Classes.Meshing;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class FEMReconstructionPageViewModel : BaseViewModel
    {
        private const int defaultMaxIterationCount = 50;
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
        private int groundElectrodeId = 0;

        [ObservableProperty]
        private double excitationCurrentAmplitude = 1.0;

        [ObservableProperty]
        private double electrodeSurfaceLength = 1.0;

        [ObservableProperty]
        private double contactImpedance = 1.0;

        [ObservableProperty]
        private double inhomogenityValue = 10.0;

        [ObservableProperty]
        private int maxIterationCount = defaultMaxIterationCount;

        [ObservableProperty]
        private double stepSize = 0.001;

        [ObservableProperty]
        private double regularizationWeight = 1e-3;

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

            var newMesh =  (FEMMesh)MeshFactory.Create(MeshType.FEM, 
                                                       layers: Layers,
                                                       boundaryVertexCount: BoundaryNodeCount, 
                                                       electrodeCount: ElectrodeCount,
                                                       inhomogenityValue: InhomogenityValue);

            _mesh = newMesh.DeepCopy();
            _reconstructedMesh = newMesh.DeepCopy();

            return newMesh;
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
                el.Potential = 0.0;
                el.ZContact = 0.1;
                el.Length = ElectrodeSurfaceLength;
                el.ZContact = ContactImpedance;
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
            var electrodes = mesh.Electrodes;

            foreach (var el in electrodes)
            {
                el.IsExcitation = false;
                el.IsGround = false;
                el.Current = 0.0;
                el.Potential = 0.0;
                el.ZContact = 0.1;
                el.Length = ElectrodeSurfaceLength;
                el.ZContact = ContactImpedance;
            }

            if (ExcitationElectrodeId == GroundElectrodeId)
                ExcitationElectrodeId++;

            electrodes[ExcitationElectrodeId % ElectrodeCount].IsExcitation = true;
            electrodes[ExcitationElectrodeId % ElectrodeCount].Current = ExcitationCurrentAmplitude;
            electrodes[GroundElectrodeId % ElectrodeCount].IsGround = true;
            electrodes[GroundElectrodeId % ElectrodeCount].Current = -ExcitationCurrentAmplitude;

            _reconstructedMesh.Electrodes = [.. electrodes];

            return _reconstructionService.SolveFemInverse(mesh,
                                                          maxIterCount: MaxIterationCount,
                                                          stepSize: StepSize,
                                                          regularization: RegularizationWeight);
        }
    }
}
