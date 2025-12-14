using System.Numerics;
using Utility.Classes.Discretizer;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;

namespace Utility.Classes.Reconstruction.DESolvers
{
    public sealed class LatticeBoltzmannDESolver : IDifferentialEquationSolver
    {
        private readonly LatticeBoltzmannSolver _solver;
        private readonly int _maxIterations;
        private readonly double _convergenceThreshold;
        private readonly int _checkInterval;
        private readonly bool _useCuda;

        public LatticeBoltzmannDESolver(int maxIterations = 2000,
                                        double convergenceThreshold = 1e-7,
                                        int checkInterval = 200,
                                        bool useCudaAcceleration = false,
                                        bool applyGaussianFilter = false,
                                        int gaussianFilterSize = 3)
        {
            _maxIterations = maxIterations;
            _convergenceThreshold = convergenceThreshold;
            _checkInterval = checkInterval;
            _useCuda = useCudaAcceleration;

            _solver = new LatticeBoltzmannSolver(_maxIterations,
                                                 _convergenceThreshold,
                                                 _checkInterval,
                                                 _useCuda,
                                                 applyGaussianFilter,
                                                 gaussianFilterSize);
        }

        public PotentialDistribution Solve(IDiscretization discretization, BoundaryCondition bc, Complex[]? adjointSource)
        {
            if (_useCuda)
            {
                return adjointSource == null
                    ? _solver.CUDASolveForward(discretization, bc)
                    : _solver.CUDASolveAdjoint(discretization, bc, adjointSource);
            }

            return adjointSource == null
                ? _solver.SolveForward(discretization, bc)
                : _solver.SolveAdjoint(discretization, bc, adjointSource);
        }

        public PotentialDistribution CUDASolveForward(IDiscretization discretization, BoundaryCondition bc)
            => _solver.CUDASolveForward(discretization, bc);

        public PotentialDistribution CUDASolveAdjoint(IDiscretization discretization, BoundaryCondition bc, Complex[] adjointSource)
            => _solver.CUDASolveAdjoint(discretization, bc, adjointSource);
    }
}
