using CommunityToolkit.Mvvm.ComponentModel;
using Google.OrTools.ConstraintSolver;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class FEMReconstructionPageViewModel : BaseReconstructionPageViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        private const int defaultMaxIterationCount = 50;

        private FEMMesh _mesh;
        private FEMMesh _reconstructedMesh;

        [ObservableProperty]
        private FEMBoundaryCondition? boundaryCondition;

        private bool _isSimulationRunning = false;

        private List<double[]> _simulatedMeasurements = [];
        private int _simulatedMeasurementsIndex = 0;

        public FEMReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            MaxIterationCount = defaultMaxIterationCount;

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
            parameters.BoundaryFEMVertexCount = BoundaryNodeCount;
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
