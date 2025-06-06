using System.Diagnostics;
using Utility.Classes.ReconstructionParameters;
using MathNet.Numerics.LinearAlgebra;

namespace Utility.Classes.Models
{
    public class InverseModel
    {
        private readonly INumericOptimizer _numericOptimizer;
        private readonly IRegularizer _regularizer;
        private readonly IErrorMetric _errorMetric;
        private readonly IDifferentialEquationSolver _deSolver;
        private readonly IMesh _mesh;

        public InverseModel(IMesh mesh, INumericOptimizer numericOptimizer, IRegularizer regularizer, IErrorMetric errorMetric, IDifferentialEquationSolver deSolver)
        {
            _mesh = mesh;
            _numericOptimizer = numericOptimizer;
            _regularizer = regularizer;
            _errorMetric = errorMetric;
            _deSolver = deSolver; 
        }

        public ConductivityDistribution Solve(ConductivityDistribution initialSigma, EITMeasurement measurements, int maxIterations = 50)
        {
            var currentSigma = initialSigma;
            var mesh = _mesh;

            Debug.WriteLine($"Starting inverse problem solution using {_deSolver.GetType().Name}...");

            for (int k = 0; k < maxIterations; k++)
            {
                var totalGradient = new Dictionary<int, double>();
                foreach (var id in currentSigma.Conductivities.Keys) 
                    totalGradient[id] = 0.0;

                double totalMisfit = 0.0;

                for(int i = 0; i < 16; i++)
                {
                    // Get current measurement that we will work with
                    double[] currentMeasurements = measurements.GetMeasurement(i);

                    // Build boundary conditions based on the measurements array
                    List<Electrode> electrodes = mesh.GetElectrodes();
                    BoundaryConditions boundaryConditions = new BoundaryConditions(electrodes, currentMeasurements);

                    var phi = _deSolver.SolveForward(mesh, currentSigma, boundaryConditions);

                    var simulatedElectrodePotentials = mesh.GetElectrodePotentials();

                    totalMisfit += _errorMetric.Evaluate(mesh, currentMeasurements, simulatedElectrodePotentials);

                    // Adjoint solve, error metric produces the rhs vector for the adjoint problem
                    var adjointSourceRaw = _errorMetric.EvaluateAdjointSource(mesh, currentMeasurements, simulatedElectrodePotentials);
                    var adjointSourceVector = MapAdjointSourceToMesh(mesh, adjointSourceRaw);
                    var mu = _deSolver.SolveAdjoint(mesh, currentSigma, boundaryConditions, adjointSourceVector);

                    // Compute gradient
                    var currentConductivityGradient = _deSolver.ComputeMisfitGradient(mesh, phi, mu);

                    foreach (var kvp in currentConductivityGradient.Conductivities)
                        totalGradient[kvp.Key] += kvp.Value;
                }

                // 4. Regularization Gradient (this is already polymorphic)
                var regGradient = _regularizer.EvaluateGradient(mesh, currentSigma);

                foreach (var kvp in regGradient.Conductivities)
                    totalGradient[kvp.Key] += kvp.Value;

                // 5. Update Conductivity
                currentSigma = _numericOptimizer.OptimizationStep(currentSigma, new ConductivityDistribution(totalGradient));

                double totalCost = totalMisfit + _regularizer.EvaluateTerm(mesh, currentSigma);
                Debug.WriteLine($"Iteration {k + 1}: Total Cost = {totalCost:G4}");
            }

            Debug.WriteLine("Inverse problem solution finished.");
            return currentSigma;
        }

        private Vector<double> MapAdjointSourceToMesh(IMesh mesh, double[] sourcePerElectrode)
        {
            var sourceVector = Vector<double>.Build.Dense(mesh.GetVertices().Count);
            var electrodeVertices = mesh.GetElectrodeVertices();

            foreach (var vertex in electrodeVertices)
            {
                if (vertex.ElectrodeId >= 0 && vertex.ElectrodeId < sourcePerElectrode.Length)
                {
                    // The negative sign comes from the adjoint equation derivation
                    sourceVector[vertex.GlobalId] = -sourcePerElectrode[vertex.ElectrodeId];
                }
            }
            return sourceVector;
        }
    }
}
