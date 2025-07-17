using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class FEMReconstructionPageViewModel : BaseViewModel
    {
        private const int defaultMaxIterationCount = 50;
        private readonly ReconstructionService _reconstructionService;
        
        [ObservableProperty]
        private int layers = 2;

        [ObservableProperty]
        private int boundaryNodeCount = 8;

        [ObservableProperty]
        private int electrodeCount = 8;

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
        private double inhomogenityValue = 2.0;

        [ObservableProperty]
        private int maxIterationCount = defaultMaxIterationCount;

        [ObservableProperty]
        private double stepSize = 0.001;

        [ObservableProperty]
        private double regularizationWeight = 1e-3;

        private FEMMesh _mesh;
        private FEMMesh _reconstructedMesh;

        private bool _isSimulationRunning = false;

        private List<double[]> _simulatedMeasurements = [];
        private int _simulatedMeasurementsIndex = 0;

        private EITReconstructionParameters reconstructionParameters = new (DifferentialEquationSolver.FiniteElementMethod, 
                                                                            RegularizationTechnique.ZeroOrderTikhonov, 
                                                                            ErrorMetric.L2, 
                                                                            NumericSolver.SVD, 
                                                                            NumericOptimizer.GradientBased);

        public FEMReconstructionPageViewModel(ReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            _mesh = GenerateMesh();
            _reconstructedMesh = GenerateMesh();

            // Initialize solver
            _reconstructionService.InitializeReconstruction(_mesh, reconstructionParameters);            
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

            _reconstructionService.InitializeReconstruction(_mesh, reconstructionParameters);

            var retMesh = _reconstructionService.SolveFemForward(mesh);;

            _reconstructedMesh.PotentialDistribution = retMesh.PotentialDistribution;

            return retMesh;
        }

        public FEMMesh SolveInverse(FEMMesh mesh)
        {
            _reconstructionService.InitializeReconstruction(_mesh, reconstructionParameters);

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

        public ReconstructionResult InverseSolveStep()
        {
            // Get the simulated measurements for the original mesh
            _simulatedMeasurements = _reconstructionService.SimulateFemMeasurements(_mesh, ExcitationCurrentAmplitude);

            // The current simulated measurement
            double[] currentSimulatedMeasurement = _simulatedMeasurements[_simulatedMeasurementsIndex % ElectrodeCount];

            // TODO: create the appropirate boundary conditions
            FEMBoundaryCondition bc = new(_mesh.Electrodes);

            ReconstructionResult reconstructionResult = _reconstructionService.InverseSolveStepFem(mesh: _mesh,
                                                                                                   measurement: currentSimulatedMeasurement,
                                                                                                   boundaryCondition: bc,
                                                                                                   stepSize: StepSize);

            _simulatedMeasurementsIndex++;

            return reconstructionResult;
        }
    }
}
