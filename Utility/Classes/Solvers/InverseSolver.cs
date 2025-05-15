using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers
{
    public class InverseSolver
    {
        private readonly IErrorMetric _metric;
        private readonly IRegularisation _regulariser;
        private readonly ILinearSolver _linSolver;
        private readonly IOptimizer _optimizer;
        private readonly ForwardSolver _forward;

        public InverseSolver(EITReconstructionParameters p)
        {
            _metric = ErrorMetricFactory.Create(p.ErrorMetric);
            _regulariser = RegularisationFactory.Create(p.RegularizationTechnique);
            _linSolver = LinearSolverFactory.Create(p.NumericSolver);
            _optimizer = OptimizerFactory.Create(p.NumericOptimizer);
            _forward = new ForwardSolver(p.DifferentialEquationSolver);
        }

        public ReconstructionResult Reconstruct(EITMeasurement data,
                                                Mesh startMesh,
                                                ConductivityDistribution initialGuess)
        {
            /*  high-level Gauss–Newton skeleton  */
            double[] σ = initialGuess.ToArray();
            Func<double[], double> objective = current =>
            {
                var sim = _forward.Solve(startMesh,
                                         ConductivityDistribution.FromArray(current),
                                         data.BoundaryConditions);

                double misfit = 0.0;
                double reg = 0.0;

                for(int i = 0; i < 13; i++)
                {
                    // Get next measurement data to compare
                    double[] currentData = data.GetMeasurement(i);

                    // Calculate the misfit with the set error metric, based on the simulated forward voltages
                    misfit = _metric.Evaluate(currentData, sim.SurfaceVoltages);

                    // Apply the regularization to the current data
                    reg = _regulariser.Apply(current).Sum(Math.Abs);
                }

                return misfit + reg;
            };

            var solution = _optimizer.Optimize(objective, σ);

            return new ReconstructionResult(mesh: startMesh, ConductivityDistribution.FromArray(solution));
        }
    }
}
