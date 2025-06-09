using System.Diagnostics;
using Utility.Classes.ReconstructionParameters;
using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Measurement;
using Utility.Classes.Factories;

namespace Utility.Classes.Models
{
    /// <summary>
    /// Orchestrates the entire EIT inverse problem.
    /// This class uses an iterative, gradient-based optimization approach to find the
    /// conductivity distribution that best explains the given measurements. It brings together
    /// the differential equation solvers, error metrics, regularizers, and numerical optimizers.
    /// </summary>
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

        /// <summary>
        /// Runs the iterative optimization loop to solve the EIT inverse problem. (Figure 2.2)
        /// </summary>
        /// <param name="initialSigma">An initial guess for the conductivity distribution.</param>
        /// <param name="measurements">The full set of EIT measurements for all drive patterns.</param>
        /// <param name="maxIterations">The maximum number of optimization iterations to perform.</param>
        /// <returns>The final, reconstructed conductivity distribution.</returns>
        public ConductivityDistribution Solve(ConductivityDistribution initialSigma, EITMeasurements measurements, int maxIterations = 50)
        {
            var currentSigma = initialSigma;
            var mesh = _mesh;

            Debug.WriteLine($"Starting inverse problem solution using {_deSolver.GetType().Name}...");

            // Calculate the cost of the initial guess. This is our starting point, J(σ_0).
            double lastCost = CalculateTotalCost(mesh, currentSigma, measurements);

            for (int k = 0; k < maxIterations; k++)
            {
                // STEP 1: COMPUTE GRADIENT
                // Calculate the "direction of steepest descent" for the total cost function.
                // This is the most computationally expensive part of each iteration.
                var totalGradient = ComputeTotalGradient(mesh, currentSigma, measurements);

                // STEP 2: FIND STEP SIZE (Line Search)
                // Determine how far to step in the gradient direction. A good step size
                // ensures that we actually decrease the cost function and don't diverge.
                //double stepSize = FindOptimalStepSize(mesh, currentSigma, measurements, totalGradient, lastCost);
                double stepSize = 1e-3;
                // STEP 3: UPDATE CONDUCTIVITY
                // Take the step to get the next conductivity estimate: σ_{k+1} = σ_k - β * ∇J
                currentSigma = _numericOptimizer.OptimizationStep(currentSigma, totalGradient, stepSize);

                // Reporting and Convergence Check 
                // Recalculate the cost at our new position for the next iteration's line search.
                lastCost = CalculateTotalCost(mesh, currentSigma, measurements);
                Debug.WriteLine($"Iteration {k + 1}: Step Size = {stepSize:E2}, Total Cost = {lastCost:G4}");

                // If the step size becomes negligible, we have likely converged to a minimum.
                if (stepSize < 1e-12)
                {
                    Debug.WriteLine("Step size is negligible. Converged.");
                    break;
                }
            }

            Debug.WriteLine("Inverse problem solution finished.");

            currentSigma.LogDistribution();

            return currentSigma;
        }

        /// <summary>
        /// Computes the total gradient of the cost function (J_misfit + J_regularization)
        /// by summing the contributions from all measurement patterns and the regularization term.
        /// </summary>
        private ConductivityDistribution ComputeTotalGradient(IMesh mesh, ConductivityDistribution sigma, EITMeasurements measurements)
        {
            // Initialize a dictionary to accumulate the gradient from all sources.
            var totalGradientDict = sigma.Conductivities.ToDictionary(kvp => kvp.Key, kvp => 0.0);

            // Loop over each of the 16 measurement drive patterns.
            for (int i = 0; i < 16; i++)
            {
                SixteenElectrodeMeasurement measurement = measurements.GetMeasurement(i);
                BoundaryConditions bc = new BoundaryConditions(mesh.GetElectrodes(), measurement);

                // === Misfit Gradient Calculation for one drive pattern ===

                // STEP 1: Solve the forward problem to get simulated potentials (φ) for the current sigma.
                var phi = _deSolver.SolveForward(mesh, sigma, bc);
                var simulatedPotentials = mesh.GetElectrodePotentials();

                // STEP 2: Clean NaN values from the potential vectors before computing the error.
                var cleanedPotentials = CleanPotentials(simulatedPotentials, measurement.Measurement);

                // STEP 3: Get the source term for the adjoint problem from the error metric.
                var adjointSourceRaw = _errorMetric.EvaluateAdjointSource(mesh, cleanedPotentials.Item2, cleanedPotentials.Item1);
                var adjointSourceVec = MapAdjointSourceToMesh(mesh, adjointSourceRaw);

                // STEP 4: Solve the adjoint problem for the sensitivity map (μ).
                var homogeneousBC = BoundaryConditionFactory.CreateHomogeneous(mesh);
                var mu = _deSolver.SolveAdjoint(mesh, sigma, homogeneousBC, adjointSourceVec);

                // STEP 5: Compute the gradient component for this measurement frame (∇φ · ∇μ).
                var misfitGradient = _deSolver.ComputeMisfitGradient(mesh, phi, mu);

                // STEP 6: Accumulate the gradient.
                foreach (var kvp in misfitGradient.Conductivities)
                    totalGradientDict[kvp.Key] += kvp.Value;
            }

            // === Regularization Gradient Calculation ===
            // After summing the misfit gradients from all patterns, add the regularization gradient.
            var regGradient = _regularizer.EvaluateGradient(mesh, sigma);
            foreach (var kvp in regGradient.Conductivities)
                totalGradientDict[kvp.Key] += kvp.Value;

            return new ConductivityDistribution(totalGradientDict);
        }

        /// <summary>
        /// Implements a backtracking line search to find a step size that provides sufficient decrease in the cost function.
        /// </summary>
        private double FindOptimalStepSize(IMesh mesh, ConductivityDistribution sigma, EITMeasurements measurements, ConductivityDistribution gradient, double currentCost)
        {
            double stepSize = 1.0; // Start with a large step size
            int maxLineSearchIter = 10;

            // Try smaller and smaller step sizes until we find one that works.
            for (int i = 0; i < maxLineSearchIter; i++)
            {
                // Get a trial conductivity estimate using the current step size.
                var newSigma = _numericOptimizer.OptimizationStep(sigma, gradient, stepSize);

                // Calculate the cost function's value at this new trial point.
                double newCost = CalculateTotalCost(mesh, newSigma, measurements);

                // Check for sufficient decrease (Armijo condition). If the new cost is lower, we've found a good step.
                if (newCost < currentCost)
                    return stepSize;

                // If no decrease, shrink the step size and try again
                stepSize *= 0.5;
            }

            // Return the smallest attempted step size if no improvement was found.
            return stepSize; 
        }

        /// <summary>
        /// Utility function to calculate the total cost J = Σ(J_misfit) + J_regularization for a given conductivity.
        /// </summary>
        private double CalculateTotalCost(IMesh mesh, ConductivityDistribution sigma, EITMeasurements measurements)
        {
            double totalMisfit = 0.0;

            // Sum the misfit over all 16 measurement patterns.
            for (int i = 0; i < 16; i++)
            {
                SixteenElectrodeMeasurement measurement = measurements.GetMeasurement(i);
                BoundaryConditions bc = new BoundaryConditions(mesh.GetElectrodes(), measurement);
                PotentialDistribution phi = _deSolver.SolveForward(mesh, sigma, bc);

                var simulated = mesh.GetElectrodePotentials();
                var cleaned = CleanPotentials(simulated, measurement.Measurement);

                totalMisfit += _errorMetric.Evaluate(mesh, cleaned.Item2, cleaned.Item1);
            }

            // Add the regularization penalty.
            double regTerm = _regularizer.EvaluateTerm(mesh, sigma);
            return totalMisfit + regTerm;
        }

        /// <summary>
        /// Replaces any NaN values in the simulated and measured potential vectors with 0.0
        /// to ensure the residual calculation is valid.
        /// </summary>
        private (double[], double[]) CleanPotentials(double[] simulated, double[] measured)
        {
            var cleanedSim = (double[])simulated.Clone();
            var cleanedMea = (double[])measured.Clone();
            for (int i = 0; i < cleanedSim.Length; i++)
            {
                if (double.IsNaN(cleanedSim[i]) || double.IsNaN(cleanedMea[i]))
                {
                    cleanedSim[i] = 0.0;
                    cleanedMea[i] = 0.0;
                }
            }
            return (cleanedSim, cleanedMea);
        }

        /// <summary>
        /// Translates the electrode-based adjoint source (the residual) into a source
        /// vector defined over the entire mesh grid. This is the 'S*' (scatter) operation.
        /// </summary>
        /// <param name="mesh">The mesh (FEM or LBM) being used.</param>
        /// <param name="sourcePerElectrode">The raw residual vector from the error metric (size 16).</param>
        /// <returns>A source vector defined for every element in the mesh.</returns>
        private Vector<double> MapAdjointSourceToMesh(IMesh mesh, double[] sourcePerElectrode)
        {
            // The source vector must match the number of elements in the grid.
            var sourceVector = Vector<double>.Build.Dense(mesh.GetElements().Count);

            // We iterate through the physical electrodes that are defined on the mesh.
            var electrodesOnMesh = mesh.GetElectrodes();
            if (electrodesOnMesh == null) return sourceVector; // Return zero vector if no electrodes

            foreach (var electrode in electrodesOnMesh)
            {
                // electrode.Id is the logical electrode number (0-15) which corresponds
                // to the index in the sourcePerElectrode array.
                int electrodeIndex = electrode.Id;

                // electrode.MeshId is the ID of the LBM element where the electrode is placed.
                // This is the index in our full sourceVector.
                int elementId = electrode.MeshId;

                if (electrodeIndex < sourcePerElectrode.Length && elementId < sourceVector.Count)
                {
                    // Place the source term at the correct element location.
                    // The negative sign comes from the adjoint PDE formula: Source = -S*(Sφ - d)
                    sourceVector[elementId] = -sourcePerElectrode[electrodeIndex];
                }
            }

            return sourceVector;
        }
    }
}
