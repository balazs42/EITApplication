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

        public IEnumerable<DifferentialEquationSolver> DifferentialEquationSolverOptions
            => Enum.GetValues(typeof(DifferentialEquationSolver))
                   .Cast<DifferentialEquationSolver>();

        public IEnumerable<RegularizationTechnique> RegularizationTechniqueOptions
            => Enum.GetValues(typeof(RegularizationTechnique))
                   .Cast<RegularizationTechnique>();

        public IEnumerable<ErrorMetric> ErrorMetricOptions
            => Enum.GetValues(typeof(ErrorMetric))
                   .Cast<ErrorMetric>();

        public IEnumerable<NumericSolver> NumericSolverOptions
            => Enum.GetValues(typeof(NumericSolver))
                   .Cast<NumericSolver>();

        public IEnumerable<NumericOptimizer> NumericOptimizerOptions
            => Enum.GetValues(typeof(NumericOptimizer))
                   .Cast<NumericOptimizer>();

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters = new(
            DifferentialEquationSolver.FiniteElementMethod,
            RegularizationTechnique.ZeroOrderTikhonov,
            ErrorMetric.L2,
            NumericSolver.SVD,
            NumericOptimizer.GradientBased);

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

        [ObservableProperty]
        private FEMBoundaryCondition? boundaryCondition;

        private bool _isSimulationRunning = false;

        private List<double[]> _simulatedMeasurements = [];
        private int _simulatedMeasurementsIndex = 0;

        public FEMReconstructionPageViewModel(ReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            _mesh = GenerateMesh();
            _reconstructedMesh = GenerateMesh();

            // Initialize solver
            _reconstructionService.InitializeReconstruction(_mesh, ReconstructionParameters);
        }


        public FEMMesh GenerateMesh()
        {
            if (BoundaryNodeCount < ElectrodeCount)
                ElectrodeCount = BoundaryNodeCount;

            MeshParameters parameters = new MeshParameters();
            parameters.MeshType = MeshType.FEM;
            parameters.Layers = Layers;
            parameters.BoundaryVertexCount = BoundaryNodeCount;
            parameters.ElectrodeCount = ElectrodeCount;

            var newMesh =  (FEMMesh)MeshFactory.Create(parameters, InhomogenityValue);

            _mesh = (FEMMesh)newMesh.DeepCopy();
            _reconstructedMesh = (FEMMesh)newMesh.DeepCopy();

            return newMesh;
        }

        public FEMMesh GetMesh() => _mesh;
        public FEMMesh GetReconstructionMesh() => _reconstructedMesh;

        public FEMMesh SolveForward(FEMMesh mesh)
        {
            if (BoundaryCondition != null)
            {
                var bcElectrodes = BoundaryCondition.GetElectrodes().Cast<FEMElectrode>().ToList();
                mesh.SetElectrodes(bcElectrodes);
            }
            else
            {
                var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

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

                mesh.SetElectrodes(electrodes);
            }

            _reconstructionService.InitializeReconstruction(_mesh, ReconstructionParameters);

            var retMesh = _reconstructionService.SolveFemForward(mesh);

            _reconstructedMesh.SetPotentialDistribution(retMesh.PotentialDistribution);

            return retMesh;
        }

        public FEMMesh SolveInverse(FEMMesh mesh)
        {
            _reconstructionService.InitializeReconstruction(_mesh, ReconstructionParameters);

            if (BoundaryCondition != null)
            {
                var bcElectrodes = BoundaryCondition.GetElectrodes().Cast<FEMElectrode>().ToList();
                _reconstructedMesh.SetElectrodes(bcElectrodes);
                mesh.SetElectrodes(bcElectrodes);
            }
            else
            {
                var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

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

                _reconstructedMesh.SetElectrodes(electrodes);
                mesh.SetElectrodes(electrodes);
            }

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
            FEMBoundaryCondition bc = BoundaryCondition ?? new FEMBoundaryCondition(_mesh.GetElectrodes().Cast<FEMElectrode>().ToList());

            ReconstructionResult reconstructionResult = _reconstructionService.InverseSolveStepFem(mesh: _mesh,
                                                                                                   measurement: currentSimulatedMeasurement,
                                                                                                   boundaryCondition: bc,
                                                                                                   stepSize: StepSize);

            _simulatedMeasurementsIndex++;

            return reconstructionResult;
        }

        public void ApplyBoundaryCondition(FEMBoundaryCondition bc)
        {
            BoundaryCondition = bc;
            var electrodes = bc.GetElectrodes().Cast<FEMElectrode>().ToList();
            _mesh.SetElectrodes(electrodes);
            _reconstructedMesh.SetElectrodes(electrodes);
        }
    }
}
