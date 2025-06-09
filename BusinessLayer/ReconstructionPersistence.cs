using Utility.Classes;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Models;
using Utility.Classes.Factories;
using Utility.Classes.Meshing;
using DataAccessLayer;
using Utility.Classes.Measurement;

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

            _inverseModel.Solve(initialDistribution, measurement, 50);

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

        private EITMeasurements SimulateMeasurement()
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
