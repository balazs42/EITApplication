using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Models
{
    public class InverseModel
    {
        private readonly INumericOptimizer _numericOptimizer;
        private readonly IRegularizer _regulizer;
        private readonly IErrorMetric _errorMetric;
        private readonly INumericSolver _numericSolver;
        private readonly ForwardModel _forwardModel;
        private readonly EITMeasurement _eITMeasurement;

        private const double _convergenceThreshold = 1e-6;

        public InverseModel(INumericOptimizer numericOptimizer, IRegularizer regulizer, IErrorMetric errorMetric, INumericSolver numericSolver, ForwardModel forwardModel, EITMeasurement eITMeasurement)
        {
            _numericOptimizer = numericOptimizer;
            _regulizer = regulizer;
            _errorMetric = errorMetric;
            _numericSolver = numericSolver;
            _forwardModel = forwardModel;
            _eITMeasurement = eITMeasurement;
        }

        public double[] Iterate()
        {
            double convergenceMeasure = 1.0;

            while (convergenceMeasure > _convergenceThreshold)
            {
                for (int i = 0; i < 16; i++)
                {
                    // Get next measurement to work with   
                    double[] currentMeasurement = _eITMeasurement.GetMeasurement(i);

                    // Use the forward model to calculate nodal potentials at the boundary
                    PotentialDistribution forwardSolution = _forwardModel.ForwardSolve();

                    // Use the error metric to come up with a measure of error
                    // TODO: this also should return something
                    convergenceMeasure = _errorMetric.Evaluate(currentMeasurement, new double[1]/*TODO: calculated*/);

                    // Inverse solve step, to calculate the adjoint variable
                    double[] inverseSolveResult = InverseSolveStep();

                    // TODO: further implementation
                }
            }

            throw new NotImplementedException();
        }

        // TODO: implement
        private double[] InverseSolveStep()
        {
            double[] result = new double[1];

            throw new NotImplementedException();
            // _numericSolver.SolveLinearSystem();

        }
    }
}
