using DataAccessLayer;
using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing;
using Utility.Classes.Models;
using Utility.Classes.ReconstructionParameters;

namespace BusinessLayer
{
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        private readonly IDAQRepository _daqRepository;

        private InverseModel _inverseModel;

        private EITMeasurements _measurementData;

        private IMesh _mesh;
        private INumericSolver _numericSolver;
        private IDifferentialEquationSolver _differentialEquationSolver;
        private IRegularizer _regularizer;
        private IErrorMetric _errorMetric;
        private INumericOptimizer _numericOptimizer;

        public ReconstructionPersistence(IDAQRepository daqRepository)
        {
            _daqRepository = daqRepository;
        }

        public async Task<ReconstructionResult> GetReconstructionResult()
        {
            if (_inverseModel == null)
                throw new InvalidOperationException();

            // Generate initial distribution for the reconstruction process
            ConductivityDistribution initialDistribution = PriorConductivityDistributionGenerator.GenerateHomogeneousDistribution(_mesh);

            // TODO: Get the current measurement
            // EITMeasurement measurement = _daqRepository.GetEITMeasurement();

            EITMeasurements measurement = SimulateMeasurement();

            _inverseModel.Solve(initialDistribution, measurement, 100);

            /* supply real data, mesh, and initial σ */
            //var result = await Task.Run(() => 
            //    _inverseModel.Solve(initialDistribution, measurement, 50)            
            //);
            throw new NotImplementedException();
            ConductivityDistribution result = new ConductivityDistribution(new());
            ReconstructionResult reconstructionResult = new ReconstructionResult((_mesh is FEMMesh) ? (FEMMesh)_mesh : (LBMMesh)_mesh, result );
            return reconstructionResult;
        }

        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters)
        {
            _mesh = mesh;

            _numericSolver = NumericSolverFactory.Create(parameters.NumericSolver);
            _differentialEquationSolver = DifferentialEquationSolverFactory.Create(parameters.DifferentialEquationSolver, _numericSolver);
            _regularizer = RegularisationFactory.Create(parameters.RegularizationTechnique, _mesh);
            _errorMetric = ErrorMetricFactory.Create(parameters.ErrorMetric);
            _numericOptimizer = NumericOptimizerFactory.Create(parameters.NumericOptimizer);

            _inverseModel = InverseModelFactory.Create(_mesh, _numericOptimizer, _regularizer, _errorMetric, _differentialEquationSolver);
        }
        /// <summary>
        /// Creates a realistic, synthetic EIT measurement by simulating the physics
        /// on a known "ground truth" conductivity map.
        /// </summary>
        private EITMeasurements SimulateMeasurement()
        {
            Debug.WriteLine("Simulating measurement data from phantom...");

            // --- Step 1: Create the Ground Truth Phantom ---
            // We use an LBMMesh for the simulation.
            var groundTruthMesh = new LBMMesh(15, 15);

            // Create a custom conductivity map with a high-conductivity rectangle inside.
            var conductivityDict = new Dictionary<int, double>();
            int centerX = groundTruthMesh.Nx / 2;
            int centerY = groundTruthMesh.Ny / 2;
            int featureSize = 3;
            foreach (var element in groundTruthMesh.Elements.Cast<LBMElement>())
            {
                var (x, y) = groundTruthMesh.ToLattice(element.Id);
                bool isFeature = Math.Abs(x - centerX) < featureSize && Math.Abs(y - centerY) < featureSize;
                conductivityDict[element.Id] = isFeature ? 0.001 : 1.0;
            }
            var groundTruthConductivity = new ConductivityDistribution(conductivityDict);

            groundTruthMesh.ConductivityDistribution = groundTruthConductivity;

            // --- Step 2: Simulate the 16 Drive Patterns ---
            const int numElectrodes = 16;
            var measurementMatrix = new double[numElectrodes, numElectrodes];

            // Get the list of physical electrodes that were created with the mesh.
            var physicalElectrodes = groundTruthMesh.GetElectrodes();

            for (int i = 0; i < numElectrodes; i++)
            {
                // For each drive pattern, we need to configure the specific currents.
                int sourceId = i;
                int sinkId = (i + 1) % numElectrodes;

                var dummyMeas = SimulateDummyMeasurement().GetMeasurement(i);

                var bc = new BoundaryConditions(physicalElectrodes, dummyMeas);

                // Run the LBM forward solver using the ground truth conductivity and this drive pattern.
                var potentialDistribution = _differentialEquationSolver.SolveForward(groundTruthMesh, groundTruthConductivity, bc);

                // After the solve, the GetElectrodePotentials method will return the calculated
                // potentials, including NaNs for the driving electrodes.
                double[] resultingPotentials = groundTruthMesh.GetElectrodePotentials();

                // Copy this result into the correct row of our final 16x16 matrix.
                for (int j = 0; j < numElectrodes; j++)
                {
                    measurementMatrix[i, j] = resultingPotentials[j];
                }

                Debug.WriteLine("Calculated Potential Field:");
                potentialDistribution.LogDistribution();
                Debug.WriteLine("---------------------------");
            }

            Debug.WriteLine("Finished simulating measurement data.");

            // --- Step 3: Wrap the final matrix in the EITMeasurement object ---
            return new EITMeasurements(measurementMatrix);
        }


        private EITMeasurements SimulateDummyMeasurement()
        {
            const int size = 16;

            double[,] meas = new double[size, size];
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    meas[i, j] = 1.0;

            // Loop through each row to place the three NaN values.
            for (int i = 0; i < size; i++)
            {
                // For each row 'i', the NaN values are at columns i, i-1, and i+1.
                // We use modulo arithmetic to handle the "wrap-around" at the edges.

                // The main diagonal index is simply 'i'.
                int diagonalIndex = i;

                // The index for the diagonal "above" the main one.
                // (15 + 1) % 16 = 0, so it correctly wraps around.
                int upperIndex = (i + 1) % size;

                // The index for the diagonal "below" the main one.
                // (0 - 1 + 16) % 16 = 15, so it correctly wraps around.
                int lowerIndex = (i - 1 + size) % size;

                meas[i, diagonalIndex] = double.NaN;
                meas[i, upperIndex] = double.NaN;
                meas[i, lowerIndex] = double.NaN;
            }

            return new EITMeasurements(meas);
        }
    }
}
