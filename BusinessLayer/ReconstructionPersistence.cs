using DataAccessLayer;
using System.Diagnostics;
using System.Numerics;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.Models;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;

namespace BusinessLayer
{
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        private readonly IDAQRepository _daqRepository;
        private readonly IReconstructionRepository _reconstructionRepository;

        private InverseModel? _inverseModel = null;
        
        private IMesh? _mesh = null;
        private INumericSolver? _numericSolver = null;
        private IDifferentialEquationSolver? _differentialEquationSolver = null;
        private IRegularizer? _regularizer = null;
        private IErrorMetric? _errorMetric = null;
        private INumericOptimizer? _numericOptimizer = null;

        private double _gradientStepSize = 0.001;
        private double _regularizationWeight = 0.001;
        private InitialDistributionTypes _initialDistributionType = InitialDistributionTypes.SlightlyDiffering;

        private bool _initialized = false;

        // --- Background reconstruction bookkeeping ---
        // Holds the running reconstruction task.  The task performs full
        // cycles of the inverse solver until a stop is requested.
        private Task<ReconstructionResult>? _backgroundTask;

        // Flag set by the Stop() method.  When true the background task
        // finishes the current cycle and then returns the accumulated
        // reconstruction result.
        private bool _stopRequested = false;

        public ReconstructionPersistence(IDAQRepository daqRepository, IReconstructionRepository reconstructionRepository)
        {
            _daqRepository = daqRepository;
            _reconstructionRepository = reconstructionRepository;
        }

        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters, bool reinit)
        {
            if(!_initialized || reinit)
            {
                _mesh = mesh;

                _numericSolver = NumericSolverFactory.Create(parameters.NumericSolver);
                _differentialEquationSolver = DifferentialEquationSolverFactory.Create(mesh, parameters.DifferentialEquationSolver, _numericSolver);
                _regularizer = RegularisationFactory.Create(parameters.RegularizationTechnique, _mesh);
                _errorMetric = ErrorMetricFactory.Create(parameters.ErrorMetric);
                _initialDistributionType = parameters.InitialDistributionType;
                _numericOptimizer = NumericOptimizerFactory.Create(parameters.NumericOptimizer, ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType));

                _inverseModel = InverseModelFactory.Create(_mesh, _numericOptimizer, _regularizer, _errorMetric, _differentialEquationSolver);

                _initialized = true;
            }
        }

        public ReconstructionFrame Step(double[] measurement, BoundaryCondition boundaryCondition, double gradientStepSize, double redularizationStepSize)
        {
            if (_mesh is FEMMesh femMesh)
            {
                FEMBoundaryCondition bc = boundaryCondition as FEMBoundaryCondition ?? throw new ArgumentException("Cannot convert boundary condition to FEM boundary condition, check calling code!");

                return InverseSolveStepFem(femMesh, bc, measurement, gradientStepSize);
            }
            else if (_mesh is LBMMesh lbmMesh)
            {
                LBMBoundaryCondition bc = boundaryCondition as LBMBoundaryCondition ?? throw new ArgumentException("Cannot convert boundary condition to LBM boundary condition, check calling code!");

                return InverseSolveStepLbm(lbmMesh, bc, measurement);
            }
            else throw new ArgumentOutOfRangeException();
        }

        public void Run(int maxIterationCount, double gradientStepSize, double redularizationStepSize)
        {
            if (!_initialized || _mesh == null)
                throw new InvalidOperationException("Reconstruction must be initialised before calling Run().");

            // Store the user supplied step sizes so the background task can
            // access them while updating the conductivity distribution.
            _gradientStepSize = gradientStepSize;
            _regularizationWeight = redularizationStepSize;

            // Reset the stop flag in case a previous reconstruction has been
            // executed.
            _stopRequested = false;

            // Spawn the background task.  Depending on the mesh type the
            // appropriate reconstruction routine is executed.  The task keeps
            // running until Stop() sets the _stopRequested flag or the
            // maximum iteration count is reached.
            _backgroundTask = Task.Run(() =>
            {
                if (_mesh is FEMMesh femMesh)
                    return RunFemReconstruction(femMesh, maxIterationCount);
                else if (_mesh is LBMMesh lbmMesh)
                    return RunLbmReconstruction(lbmMesh, maxIterationCount);
                else
                    throw new ArgumentOutOfRangeException("Unsupported mesh type for reconstruction.");
            });
        }

        public ReconstructionResult Stop()
        {
            if (_backgroundTask == null)
                throw new InvalidOperationException("Run() must be called before Stop().");

            // Signal the background task to finish the current iteration and
            // exit gracefully.  The task checks the _stopRequested flag at the
            // start of every cycle.
            _stopRequested = true;

            // Wait for the task to complete and return the final reconstruction
            // result.  Using GetAwaiter().GetResult() avoids AggregateException
            // wrapping and propagates the original exception if one occurred.
            var result = _backgroundTask.GetAwaiter().GetResult();

            // Clear task reference so a new reconstruction can be started.
            _backgroundTask = null;

            return result;
        }

        public PotentialDistribution ForwardSolveStepFem()
        {
            if (_mesh is not FEMMesh mesh)
                throw new TypeInitializationException("Mesh should be of type FEMMesh to use FEM solver!", new Exception("Invalid type in solver!"));

            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            FEMBoundaryCondition boundaryConditions = new(electrodes);

            return _differentialEquationSolver.Solve(mesh, boundaryConditions, null);
        }

        public PotentialDistribution ForwardSolveStepLbm()
        {
            if (_mesh is not LBMMesh mesh)
                throw new TypeInitializationException("Mesh should be of type LBMMesh to use LBM solver!", new Exception("Invalid type in solver!"));

            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();

            LBMBoundaryCondition boundaryConditions = new(electrodes);

            return _differentialEquationSolver.Solve(_mesh, boundaryConditions, null);

        }

        public ReconstructionFrame InverseSolveStepFem(FEMMesh mesh, FEMBoundaryCondition bc, double[] currentMeasurement, double gradientStepSize)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            if(_errorMetric == null)
                throw new NullReferenceException("Error metric is null, check calling code!");

            if (_regularizer == null)
                throw new NullReferenceException("Regularizer is null, check calling code!");

            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            // Solve Forward to extract simulated potentials
            PotentialDistribution phi = _differentialEquationSolver.Solve(mesh, bc, null);

            // Extract simulated potentials
            double[] simulatedPotentials = mesh.GetElectrodePotentials();

            // Calculate current error
            double currentError = _errorMetric.Evaluate(mesh, currentMeasurement, simulatedPotentials);

            // Error metric based gradeint expression for the adjoint equation
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, currentMeasurement, simulatedPotentials);

            // Create the new adjoint source array
            Complex[] adjointSource = new Complex[adjSrc.Length];
            for (int k = 0; k < adjSrc.Length; k++)
                adjointSource[k] = adjSrc[k];

            // Solve the adjoint equation with the new boundary condition
            PotentialDistribution mu = _differentialEquationSolver.Solve(mesh, new FEMBoundaryCondition(electrodes), adjointSource);

            // Gradient expression for the conductivity field
            ConductivityDistribution dataGrad = new ConductivityDistribution(
                mesh.GetElements().Cast<FEMElement>().ToDictionary(
                    el => el.Id,
                    el => {
                        // compute ∇φ, ∇μ on this element
                        var gPhi = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi)
                                    .GetVector(el.Id);
                        var gMu = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu)
                                    .GetVector(el.Id);
                        return (gMu.X * gPhi.X + gMu.Y * gPhi.Y) * el.Area;
                    }
                )
            );

            // Apply gradient step size
            foreach (var kvp in dataGrad.Conductivities)
                dataGrad.Conductivities[kvp.Key] = kvp.Value * gradientStepSize;

            ConductivityDistribution sigma = mesh.GetConductivityDistribution();

            // Calculate the regularization field
            double regTerm = _regularizer.EvaluateTerm(mesh, sigma);
            ConductivityDistribution regularization = _regularizer.EvaluateGradient(mesh, sigma);            

            // Returning the partial results from the inverse calculations
            return new ReconstructionFrame(dataGrad, phi, mu, regularization);
        }

        public ReconstructionFrame InverseSolveStepLbm(LBMMesh mesh, LBMBoundaryCondition bc, double[] currentMeasurement)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            if (_errorMetric == null)
                throw new NullReferenceException("Error metric is null, check calling code!");

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();

            // Solve Forward to extract simulated potentials
            PotentialDistribution phi = _differentialEquationSolver.Solve(mesh, bc, null);

            // Extract simulated potentials
            double[] simulatedPotentials = mesh.GetElectrodePotentials();

            double currentError = _errorMetric.Evaluate(mesh, currentMeasurement, simulatedPotentials);

            // Error metric based gradeint expression
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, currentMeasurement, simulatedPotentials);

            Complex[] adjointSource = new Complex[adjSrc.Length];
            for (int k = 0; k < adjSrc.Length; k++)
                adjointSource[k] = adjSrc[k];

            PotentialDistribution mu = _differentialEquationSolver.Solve(mesh, new LBMBoundaryCondition(electrodes), adjointSource);

            ConductivityDistribution dataGrad = new ConductivityDistribution(
                mesh.GetElements().ToDictionary(
                    el => el.Id,
                    el => {
                        // compute <∇φ, ∇μ> on this element
                        var gPhi = LatticeBoltzmannOperators.CalculateGradient(mesh, phi).GetVector(el.Id);
                        var gMu = LatticeBoltzmannOperators.CalculateGradient(mesh, mu).GetVector(el.Id);
                        return (gMu.X * gPhi.X + gMu.Y * gPhi.Y);
                    }
                )
            );

            return new ReconstructionFrame(dataGrad, phi, mu, new ConductivityDistribution(new()));
        }

        // ------------------------------------------------------------------
        //  Private background task implementations
        // ------------------------------------------------------------------

        /// <summary>
        ///     Runs the complete FEM reconstruction in a background task.
        ///     Each iteration excites neighbouring electrode pairs, performs
        ///     a forward and adjoint solve and accumulates the resulting
        ///     gradient.  After all electrode pairs are processed the
        ///     regularization term is added, the gradient is normalised and the
        ///     conductivity distribution is updated.
        /// </summary>
        /// <param name="mesh">Finite element mesh being reconstructed.</param>
        /// <param name="maxIterationCount">Maximum number of reconstruction
        ///     cycles to execute.</param>
        /// <returns>The final reconstruction result produced by the task.</returns>
        private ReconstructionResult RunFemReconstruction(FEMMesh mesh, int maxIterationCount)
        {
            if (_errorMetric == null || _regularizer == null)
                throw new NullReferenceException("Error metric or regularizer not initialised.");

            // --- Prepare reference data --------------------------------------------------

            // Save the original distribution for the ReconstructionResult and
            // generate synthetic measurements that represent the observed data
            // for the inverse problem.
            ConductivityDistribution originalSigma = mesh.DeepCopy().GetConductivityDistribution();
            List<double[]> measurementFrames = SimulateFemMeasurements(mesh, 1.0);

            // Replace the mesh conductivity with the user-selected initial
            // distribution so that the simulated data differs from the
            // observed measurements, yielding a non-zero adjoint source.
            ConductivityDistribution initialSigma = ConductivityDistributionFactory
                                                        .CreateInitialDistribution(mesh, _initialDistributionType);
            mesh.SetConductivityDistribution(initialSigma);

            // Cache electrode and element information for repeated use.
            List<FEMElectrode> electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            int electrodeCount = electrodes.Count;

            var elements = mesh.GetElements().Cast<FEMElement>().ToList();

            // Container that stores intermediate frames for later inspection.
            List<ReconstructionFrame> frames = [];

            // --- Iterative reconstruction loop -----------------------------------------
            for (int iter = 0; iter < maxIterationCount && !_stopRequested; iter++)
            {
                // Initialise an accumulator for the gradient contributions of
                // all electrode pairs.
                Dictionary<int, double> totalGrad = elements.ToDictionary(el => el.Id, _ => 0.0);

                for (int exc = 0; exc < electrodeCount; exc++)
                {
                    // Reset electrode roles before applying a new excitation
                    // pattern.  The excitation amplitude is set to unity for
                    // simplicity; the measurement data already includes this
                    // amplitude.
                    foreach (var el in electrodes)
                    {
                        el.Current = 0.0;
                        el.IsExcitation = false;
                        el.IsGround = false;
                        el.Potential = 0.0;
                    }

                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].Current = 1.0;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].Current = -1.0;

                    // Boundary condition reflecting the just configured
                    // electrode setup.
                    var bc = new FEMBoundaryCondition(electrodes);

                    // Measurement corresponding to this excitation pattern.
                    double[] dObs = measurementFrames[exc];

                    // Perform forward/adjoint solve and obtain the gradient
                    // contribution for this electrode pair.  The gradient step
                    // size is set to unity so the returned gradient represents
                    // ∇J_data without any scaling.
                    var frame = InverseSolveStepFem(mesh, bc, dObs, 1.0);

                    frames.Add(frame);

                    foreach (var kvp in frame.ConductivityGradient.Conductivities)
                        totalGrad[kvp.Key] += kvp.Value;
                }

                // Add regularisation gradient to the accumulated data
                // gradient and normalise by the number of electrode pairs.
                var sigma = mesh.GetConductivityDistribution();
                var regGrad = _regularizer.EvaluateGradient(mesh, sigma);
                foreach (var key in totalGrad.Keys.ToList())
                {
                    double g = totalGrad[key] + _regularizationWeight * regGrad.GetConductivity(key);
                    totalGrad[key] = g / electrodeCount;
                }

                // Update the conductivity distribution by taking a step along
                // the accumulated gradient.
                var newSigmaDict = sigma.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + _gradientStepSize * totalGrad[kvp.Key]);

                mesh.SetConductivityDistribution(new ConductivityDistribution(newSigmaDict));
            }

            // Final reconstructed distribution after termination of the loop.
            ConductivityDistribution reconstructed = mesh.GetConductivityDistribution();

            return new ReconstructionResult(mesh,
                                            originalSigma,
                                            initialSigma,
                                            reconstructed,
                                            frames);
        }

        /// <summary>
        ///     Background reconstruction routine for Lattice Boltzmann meshes.
        ///     The structure mirrors <see cref="RunFemReconstruction"/> but
        ///     utilises LBM specific data structures and operators.
        /// </summary>
        private ReconstructionResult RunLbmReconstruction(LBMMesh mesh, int maxIterationCount)
        {
            if (_errorMetric == null)
                throw new NullReferenceException("Error metric not initialised.");

            // Simulated measurement frames taken as observed data.
            EITMeasurement measurementFrames = SimulateLbmMeasurements(mesh, 1.0);

            ConductivityDistribution originalSigma = ((LBMMesh)mesh.DeepCopy()).GetConductivityDistribution();
            ConductivityDistribution initialSigma = mesh.GetConductivityDistribution();

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();
            int electrodeCount = electrodes.Count;

            var elements = mesh.GetElements().ToList();

            List<ReconstructionFrame> frames = new();

            for (int iter = 0; iter < maxIterationCount && !_stopRequested; iter++)
            {
                Dictionary<int, double> totalGrad = elements.ToDictionary(el => el.Id, _ => 0.0);

                for (int exc = 0; exc < electrodeCount; exc++)
                {
                    foreach (var el in electrodes)
                    {
                        el.Current = 0.0;
                        el.IsExcitation = false;
                        el.IsGround = false;
                        el.Potential = 0.0;
                    }

                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].Current = 1.0;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].Current = -1.0;

                    var bc = new LBMBoundaryCondition(electrodes);
                    double[] dObs = measurementFrames.Frames[exc];

                    var frame = InverseSolveStepLbm(mesh, bc, dObs);

                    frames.Add(frame);

                    foreach (var kvp in frame.ConductivityGradient.Conductivities)
                        totalGrad[kvp.Key] += kvp.Value;
                }

                var sigma = mesh.GetConductivityDistribution();
                var regGrad = _regularizer?.EvaluateGradient(mesh, sigma);
                foreach (var key in totalGrad.Keys.ToList())
                {
                    double g = totalGrad[key];
                    if (regGrad != null)
                        g += _regularizationWeight * regGrad.GetConductivity(key);
                    totalGrad[key] = g / electrodeCount;
                }

                var newSigmaDict = sigma.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + _gradientStepSize * totalGrad[kvp.Key]);

                mesh.SetConductivityDistribution(new ConductivityDistribution(newSigmaDict));
            }

            ConductivityDistribution reconstructed = mesh.GetConductivityDistribution();

            return new ReconstructionResult(mesh,
                                            originalSigma,
                                            initialSigma,
                                            reconstructed,
                                            frames);
        }

        public ReconstructionResult InverseSolveFem(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6)
        {
            if(_mesh is not FEMMesh mesh)
                throw new TypeInitializationException("Mesh should be of type FEMMesh to use FEM solver!", new Exception("Invalid type in solver!"));

            if (_regularizer == null)
                throw new NullReferenceException("Regularizer is null, check calling code!");

            if (_numericOptimizer == null)
                throw new NullReferenceException("Numeric optimizer is null, check calling code!");

            _regularizationWeight = redularizationStepSize;

            List<double[]> simulatedMeasurements = SimulateFemMeasurements(mesh, excitationAmplitude);
            ConductivityDistribution originalConductivityDistribution = mesh.DeepCopy().GetConductivityDistribution();

            // 2) Initialize conductivity (σ^{(0)}) based on user selection
            ConductivityDistribution initialConductivityDistribution = ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType);
            mesh.SetConductivityDistribution(initialConductivityDistribution);

            List<FEMElectrode> electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var bc = new FEMBoundaryCondition(electrodes);
            int electrodeCount = electrodes.Count;

            var elements = mesh.GetElements().Cast<FEMElement>().ToList();
            int elementCount = elements.Count;

            // 4) Iterative loop
            double prevJ = double.PositiveInfinity;
            List<double> errors = [];
            List<ReconstructionFrame> frames = [];

            for (int iter = 0; iter < maxIterationCount; iter++)
            {
                Debug.WriteLine($"\n=== Inverse iteration {iter} ===");

                Dictionary<int, double> totalGrad = new();
                for (int i = 0; i < elementCount; i++)
                    totalGrad.Add(i, 0.0);


                // Iterate around with the excitation electrodes
                for (int exc = 0; exc < electrodeCount; exc++)
                {
                    // Clear electrode status
                    foreach (var el in electrodes)
                    {
                        el.Current = 0.0;
                        el.IsExcitation = false;
                        el.IsGround = false;
                        el.Potential = 0.0;
                    }

                    // Set new electrode setup
                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].Current = excitationAmplitude;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].Current = -excitationAmplitude;

                    // Current simulated measurement extraction
                    double[] dObs = simulatedMeasurements[exc];

                    // Perform an inverse solve step and extract partial results
                    var frame = InverseSolveStepFem(mesh, bc, dObs, gradientStepSize);

                    // Get the gradient expression from the inverse solve step
                    var dataGrad = frame.ConductivityGradient;

                    // Add the gradient expression to the total gradient expression
                    foreach (var kvp in dataGrad.Conductivities)
                        totalGrad[kvp.Key] += kvp.Value;
                }

                // Regularization term J_reg and grad ∇J_reg (Eq. 2.1.27/2.1.28)
                double regTerm = _regularizer.EvaluateTerm(mesh, initialConductivityDistribution);
                var regGrad = _regularizer.EvaluateGradient(mesh, initialConductivityDistribution);
                Debug.WriteLine($"Regularization R = {regTerm:0.#####}");

                // Total gradient ∇J = ∇J_data + ∇R  (Eq. 2.1.31)
                var totalGradDict = totalGrad.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + _regularizationWeight * regGrad.GetConductivity(kvp.Key)
                );

                // Normalize gradient
                foreach (var kvp in totalGradDict)
                    totalGradDict[kvp.Key] = kvp.Value / simulatedMeasurements.Count;

                // Create the new conductivity field with the gradient expression
                var grad = new ConductivityDistribution(totalGradDict);

                Debug.WriteLine("Gradient ∇J computed.");

                // Apply optimization step
                var newConductivityDistribution = _numericOptimizer.OptimizationStep(mesh.ConductivityDistribution, grad, gradientStepSize);

                mesh.SetConductivityDistribution(newConductivityDistribution);

                // Compute total misfit
                double Jtotal = CalculateTotalMisiftFem(mesh, simulatedMeasurements, bc, excitationAmplitude);
                Debug.WriteLine($"Iteration {iter}: total misfit = {Jtotal}");
                errors.Add(Jtotal);

                // Check error threshold
                if (Math.Abs(prevJ - Jtotal) < tolerance)
                    break;

                prevJ = Jtotal;
            }

            Debug.WriteLine("Erorrs during iteration:");
            // print errors
            for (int i = 0; i < errors.Count; i++)
                if (i % 5 == 0)
                    Debug.WriteLine($"It[{i}]: {errors[i]:F6} ");
                else Debug.Write($"It[{i}]: {errors[i]:F6}\t");

            Debug.WriteLine("");

            // 5) Update mesh ConductivityDistribution and return
            foreach (var el in elements)
                el.Conductivity = initialConductivityDistribution.GetConductivity(el.Id);

            ConductivityDistribution reconstructedConductivityDistribution = mesh.GetConductivityDistribution();

            return new ReconstructionResult(mesh, originalConductivityDistribution, initialConductivityDistribution, reconstructedConductivityDistribution, frames);
        }

        public ReconstructionResult InverseSolveLbm(int maxIterationCount,
                                                    double gradientStepSize,
                                                    double redularizationStepSize,
                                                    double excitationAmplitude,
                                                    double tolerance = 1e-6)
        {
            if (_mesh is not LBMMesh mesh)
                throw new TypeInitializationException("Mesh should be of type LBMMesh to use LBM solver!", new Exception("Invalid type in solver!"));

            if (_regularizer == null)
                throw new NullReferenceException("Regularizer is null, check calling code!");

            _gradientStepSize = gradientStepSize;
            _regularizationWeight = redularizationStepSize;

            return RunLbmReconstruction(mesh, maxIterationCount);
        }

        private double CalculateTotalMisiftFem(FEMMesh mesh, List<double[]> simulatedMeasurements, FEMBoundaryCondition bc, double excitationAmplitude)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            if (_errorMetric == null)
                throw new NullReferenceException("Error metric is null, check calling code!");

            List<FEMElectrode> electrodes = [.. mesh.GetElectrodes().Cast<FEMElectrode>()];
            int electrodeCount = electrodes.Count;

            double Jtotal = 0;

            for (int exc = 0; exc < electrodeCount; exc++)
            {
                // Clear electrode status
                foreach (var el in electrodes)
                {
                    el.Current = 0.0;
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.Potential = 0.0;
                }

                // Set new electrode setup
                electrodes[exc % electrodeCount].IsExcitation = true;
                electrodes[exc % electrodeCount].Current = excitationAmplitude;
                electrodes[(exc + 1) % electrodeCount].IsGround = true;
                electrodes[(exc + 1) % electrodeCount].Current = -excitationAmplitude;

                var phiNew = _differentialEquationSolver.Solve(mesh, bc, null);
                double[] dSimNew = mesh.GetElectrodePotentials();
                double[] dObs = simulatedMeasurements[exc];
                Jtotal += _errorMetric.Evaluate(mesh, dObs, dSimNew);
            }

            return Jtotal;
        }


        #region Lattice Boltzmann Reconstruction

        private PotentialDistribution SolveLbmForward()
        {
            LBMMesh? mesh = _mesh as LBMMesh;

            if (_inverseModel == null || _mesh == null || mesh == null || _differentialEquationSolver == null)
                throw new NullReferenceException();

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();

            LBMBoundaryCondition bc = new(electrodes);

            return _differentialEquationSolver.Solve(_mesh, bc, null);
        }

        private ReconstructionFrame LbmSolveStep(LBMMesh mesh, LBMBoundaryCondition bc, double[] currentMeasurement)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Cannot perform solve step, DE solver is null.");

            if (_errorMetric == null)
                throw new NullReferenceException("Cannot perform solve step Error Metric is null.");

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();

            // Solve Forward to extract simulated potentials
            PotentialDistribution phi = _differentialEquationSolver.Solve(mesh, bc, null);

            // Extract simulated potentials
            double[] simulatedPotentials = mesh.GetElectrodePotentials();

            double currentError = _errorMetric.Evaluate(mesh, currentMeasurement, simulatedPotentials);

            // Error metric based gradeint expression
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, currentMeasurement, simulatedPotentials);

            Complex[] adjointSource = new Complex[adjSrc.Length];
            for (int k = 0; k < adjSrc.Length; k++)
                adjointSource[k] = adjSrc[k];

            PotentialDistribution mu = _differentialEquationSolver.Solve(mesh, new LBMBoundaryCondition(electrodes), adjointSource);

            ConductivityDistribution dataGrad = new ConductivityDistribution(
                mesh.GetElements().ToDictionary(
                    el => el.Id,
                    el => {
                        // compute <∇φ, ∇μ> on this element
                        var gPhi = LatticeBoltzmannOperators.CalculateGradient(mesh, phi).GetVector(el.Id);
                        var gMu = LatticeBoltzmannOperators.CalculateGradient(mesh, mu).GetVector(el.Id);
                        return (gMu.X * gPhi.X + gMu.Y * gPhi.Y);
                    }
                )
            );

            return new ReconstructionFrame(dataGrad, phi, mu, new ConductivityDistribution(new()));
        }

        public ReconstructionResult SolveLbmInverse(int maxIterationCount)
        {
            double stepSize = _gradientStepSize;
            LBMMesh? mesh = (_mesh as LBMMesh);

            if (_inverseModel == null || _mesh == null || mesh == null || _differentialEquationSolver == null || _errorMetric == null)
                throw new NullReferenceException();

            ConductivityDistribution originalConductivityDistribution = ((LBMMesh)mesh.DeepCopy()).GetConductivityDistribution();
            mesh = (LBMMesh)mesh.DeepCopy();

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();

            LBMBoundaryCondition bc = new(electrodes);

            // --- Simulate Measurements for the Inverse Solver ---
            EITMeasurement measurementFrames = SimulateLbmMeasurements(mesh, 1.0);

            // Container to hold partial results from reconstrucion
            List<ReconstructionFrame> frames = [];

            // --- Inverse Solver Iterations ---

            // Loop to run the inverse iterations
            for(int i = 0; i < maxIterationCount; i++)
            {
                // One iteration run on the whole measurement frame
                for(int j = 0; j < measurementFrames.Frames.Count; j++)
                {
                    double[] currentMeasurement = measurementFrames.GetNextFrame();

                    var frame = LbmSolveStep(mesh, bc, currentMeasurement);

                    // --- Set new conductivities ---
                    double step = stepSize;  
                    var sigma = mesh.GetConductivityDistribution();

                    var newSigmaDict = sigma.Conductivities.ToDictionary(
                        kvp => kvp.Key,
                        // Clamp conductivites which got below 0
                        kvp => ((kvp.Value - step * frame.ConductivityGradient.GetConductivity(kvp.Key)) < 0.0) ?
                                                                1e-1 : kvp.Value - step * frame.ConductivityGradient.GetConductivity(kvp.Key)
                    );

                    sigma = new ConductivityDistribution(newSigmaDict);
                    mesh.SetConductivityDistribution(sigma);

                    // Add partial results
                    frames.Add(frame);
                }
            }

            ConductivityDistribution reconstructedConductivityDistribution = mesh.GetConductivityDistribution();

            return new ReconstructionResult((LBMMesh)_mesh,
                                            originalConductivityDistribution,
                                            originalConductivityDistribution,
                                            reconstructedConductivityDistribution,
                                            frames);
        }

        public EITMeasurement SimulateLbmMeasurements(LBMMesh mesh, double exciationAmplitude)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver was null, check initializiation code!");

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();
            int electrodeCount = electrodes.Count;

            double[,] measurementFrames = new double[electrodeCount, electrodeCount];

            for(int i = 0; i < electrodeCount; i++)
            {
                // Set the excitation electrodes
                foreach(var el in electrodes)
                {
                    el.IsMeasuring = false;
                    el.IsGround = false;
                    el.IsExcitation = false;
                    el.Potential = 0.0;
                    el.Current = 0.0;
                }

                electrodes[i % electrodeCount].IsExcitation = true;
                electrodes[i % electrodeCount].Current = 2.0;
                electrodes[(i + 1) % electrodeCount].IsGround = true;
                electrodes[(i + 1) % electrodeCount].Current = 0.0;

                // Create boundary conditions for the solver
                LBMBoundaryCondition boundaryCondition = new LBMBoundaryCondition(electrodes);

                // Solve for the arising potentials
                _ = _differentialEquationSolver.Solve(mesh, boundaryCondition, null);
                
                // Extract simulated potentials
                double[] electrodePotentials = mesh.GetElectrodePotentials();

                for (int j = 0; j < electrodeCount; j++)
                    measurementFrames[i, j] = electrodePotentials[j];
            }

            return new EITMeasurement(measurementFrames);
        }

        #endregion

        #region Finite Element Method Reconstrucion

        public FEMMesh SolveFemForward(FEMMesh mesh)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Cannot perform Finite Element forward solve, differential equation solver is not specified!");

            var conductivitiyDistribution = mesh.GetConductivityDistribution();

            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            BoundaryCondition boundaryConditions = new FEMBoundaryCondition(electrodes);

            PotentialDistribution potentialDistribution = _differentialEquationSolver.Solve(mesh, boundaryConditions, null);
            mesh.SetPotentialDistribution(potentialDistribution);

            return mesh;
        }

        public FEMMesh SolveFemInverse(FEMMesh mesh, int maxIterCount, double stepSize, double regularization)
        {
            _regularizationWeight = regularization;
            return SolveFemInverseAllFrames(mesh, maxIterCount, stepSize, regularization);

            // Forward step computes the correct potential values
            FEMMesh forwardProjection = SolveFemForward(mesh);
            double[] measuredValues = forwardProjection.GetElectrodePotentials();

            Debug.WriteLine("The simulated measured values are:");
            for (int i = 0; i < measuredValues.Length; i++)
                Debug.WriteLine($"{measuredValues[i]}");

            // 2) Initialize conductivity (σ^{0}) to homogeneous distribution
            ConductivityDistribution sigma = ConductivityDistributionFactory.CreateRandom(mesh);
            mesh.SetConductivityDistribution(sigma);
            
            // 3) Mark electrodes: 0=ground, 1=excitation
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var bc = new FEMBoundaryCondition(electrodes);

            // 4) Iterative loop
            double prevError = double.PositiveInfinity;
            List<double> errors = [];

            for (int iter = 0; iter < maxIterCount; iter++)
            {
                Debug.WriteLine($"\n=== Inverse iteration {iter} ===");

                // 4a) Forward solve φ⁽ᵏ⁾ = S(σ⁽ᵏ⁾)   (thesis Eq. 1.1.16)
                PotentialDistribution phi = _differentialEquationSolver.Solve(mesh, bc, null);
                Debug.WriteLine("Forward φ computed.");

                // 4b) Extract simulated boundary data d_sim
                double[] dSim = mesh.GetElectrodePotentials();
                Debug.WriteLine("The simulated electrode potentials during iteration:");
                for (int i = 0; i < dSim.Length; i++)
                    Debug.WriteLine($"{dSim[i]}");

                double[] dObs = measuredValues;

                // 4c) Compute misfit J_misfit (thesis Eq. 2.1.4 or 3.1.1)
                double misfit = _errorMetric.Evaluate(mesh, dObs, dSim);
                Debug.WriteLine($"Misfit J = {misfit:0.#####}");

                // 4d) Regularization term J_reg and grad ∇J_reg (Eq. 2.1.27/2.1.28)
                double regTerm = _regularizer.EvaluateTerm(mesh, sigma);
                var regGrad = _regularizer.EvaluateGradient(mesh, sigma);
                Debug.WriteLine($"Regularization R = {regTerm:0.#####}");

                // 4e) Build adjoint source s = EvaluateAdjointSource (L2: residual; W2: Kantorovich φ) 
                var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, dObs, dSim);
                // wrap into a PotentialDistribution on electrodes
                var srcDist = new PotentialDistribution(
                    Enumerable.Range(0, adjSrc.Length)
                              .ToDictionary(i => electrodes[i].Id, i => adjSrc[i])
                );

                // 4f) Adjoint solve μ: same forward‐solver but feed in adjSrc as boundary currents
                var mu = _differentialEquationSolver
                    .Solve(mesh,
                                  new FEMBoundaryCondition(electrodes, srcDist), null /*TODO: this should be adjoint source*/ );
                Debug.WriteLine("Adjoint μ computed.");

                // 4g) Compute gradient ∇J_data = ∇μ·∇φ elementwise  (thesis Eq. 2.1.20)
                var dataGrad = new ConductivityDistribution(
                    mesh.GetElements().Cast<FEMElement>().ToDictionary(
                        el => el.Id,
                        el => {
                            // compute ∇φ, ∇μ on this element
                            var gPhi = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi)
                                        .GetVector(el.Id);
                            var gMu = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu)
                                        .GetVector(el.Id);
                            return (gMu.X * gPhi.X + gMu.Y * gPhi.Y) * el.Area;
                        }
                    )
                );

                // 4h) Total gradient ∇J = ∇J_data + ∇R  (Eq. 2.1.31)
                var totalGradDict = dataGrad.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + regGrad.GetConductivity(kvp.Key)
                );
                var totalGrad = new ConductivityDistribution(totalGradDict);

                Debug.WriteLine("Gradient ∇J computed.");

                // 4i) Line search / simple step: σ⁽ᵏ⁺¹⁾ = σ⁽ᵏ⁾ - α ∇J
                double step = stepSize;  // choose small enough for stability
                var newSigmaDict = sigma.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    // Clamp conductivites which got below 0
                    kvp => ((kvp.Value - step * totalGrad.GetConductivity(kvp.Key)) < 0.0) ? 1e-1 : kvp.Value - step * totalGrad.GetConductivity(kvp.Key)
                );
                
                sigma = new ConductivityDistribution(newSigmaDict);
                mesh.SetConductivityDistribution(sigma);

                // 4j) Check convergence on boundary misfit change
                if (Math.Abs(prevError - misfit) < 1e-8)
                {
                    Debug.WriteLine("Converged on misfit change. Stopping.");
                    break;
                }
                prevError = misfit;
                errors.Add(prevError);
            }

            Debug.WriteLine("Erorrs during iteration:");
            // print errors
            for (int i = 0; i < errors.Count; i++)
                if (i % 10 == 0)
                    Debug.WriteLine($"{errors[i]:F6} ");
                else Debug.Write($"{errors[i]:F6}, ");

            var elements = mesh.GetElements();

            // 5) Update mesh ConductivityDistribution and return
            foreach (var el in elements)
                el.Conductivity = sigma.GetConductivity(el.Id);

            return mesh;
        }

        public FEMMesh SolveFemInverseAllFrames(FEMMesh mesh, int maxIterCount, double stepSize, double regularization, double excitationAmplitude = 1.0, double tolerance = 1e-5, double minConductivtiy = 1e-3, double maxConductivity = 10.0)
        {
            if (_differentialEquationSolver == null ||
                _errorMetric == null ||
                _regularizer == null ||
                _numericOptimizer == null)
                throw new NullReferenceException("Some solver parameter is null, the solver must properly be initialized, throguh the layer, check code!");

            _regularizationWeight = regularization;

            List<double[]> simulatedMeasurements = SimulateFemMeasurements(mesh, excitationAmplitude);
            
            // 2) Initialize conductivity (σ^{(0)}) based on user selection
            ConductivityDistribution sigma = ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType);
            mesh.SetConductivityDistribution(sigma);

            List<FEMElectrode> electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var bc = new FEMBoundaryCondition(electrodes);
            int electrodeCount = electrodes.Count;

            var elements = mesh.GetElements().Cast<FEMElement>().ToList();
            int elementCount = elements.Count;

            // 4) Iterative loop
            double prevJ = double.PositiveInfinity;
            List<double> errors = [];

            // TODO: Add partial results

            for (int iter = 0; iter < maxIterCount; iter++)
            {
                Debug.WriteLine($"\n=== Inverse iteration {iter} ===");

                Dictionary<int, double> totalGrad = new();
                for (int i = 0; i < elementCount; i++)
                    totalGrad.Add(i, 0.0);

                for(int exc = 0; exc < electrodeCount; exc++)
                {
                    // Clear electrode status
                    foreach (var el in electrodes)
                    {
                        el.Current = 0.0;
                        el.IsExcitation = false;
                        el.IsGround = false;
                        el.Potential = 0.0;
                    }

                    // Set new electrode setup
                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].Current = excitationAmplitude;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].Current = -excitationAmplitude;

                    bc = new FEMBoundaryCondition(electrodes);

                    // 4a) Forward solve φ⁽ᵏ⁾ = S(σ⁽ᵏ⁾)   (thesis Eq. 1.1.16)
                    PotentialDistribution phi = _differentialEquationSolver.Solve(mesh, bc, null);
                    Debug.WriteLine("Forward φ computed.");

                    // 4b) Extract simulated boundary data d_sim
                    double[] dSim = mesh.GetElectrodePotentials();
                    Debug.WriteLine("The simulated electrode potentials during iteration:");
                    for (int i = 0; i < dSim.Length; i++)
                        Debug.WriteLine($"{dSim[i]}");

                    double[] dObs = simulatedMeasurements[exc];

                    // 4e) Build adjoint source s = EvaluateAdjointSource (L2: residual; W2: Kantorovich φ) 
                    var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, dObs, dSim);

                    Complex[] adjointSource = new Complex[adjSrc.Length];
                    for(int i = 0; i < adjSrc.Length; i++)
                        adjointSource[i] = adjSrc[i];


                    // wrap into a PotentialDistribution on electrodes
                    var srcDist = new PotentialDistribution(
                        Enumerable.Range(0, adjSrc.Length)
                                  .ToDictionary(i => electrodes[i].Id, i => adjSrc[i])
                    );

                    // 4f) Adjoint solve μ: same forward‐solver but feed in adjSrc as boundary currents
                    var mu = _differentialEquationSolver.Solve(mesh, new FEMBoundaryCondition(electrodes, srcDist), adjointSource);
                    Debug.WriteLine("Adjoint μ computed.");

                    // 4g) Compute gradient ∇J_data = ∇μ·∇φ elementwise  (thesis Eq. 2.1.20)
                    var dataGrad = new ConductivityDistribution(
                        mesh.GetElements().Cast<FEMElement>().ToDictionary(
                            el => el.Id,
                            el => {
                                // compute ∇φ, ∇μ on this element
                                var gPhi = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi)
                                            .GetVector(el.Id);
                                var gMu = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu)
                                            .GetVector(el.Id);
                                return (gMu.X * gPhi.X + gMu.Y * gPhi.Y) * el.Area;
                            }
                        )
                    );

                    foreach (var kvp in dataGrad.Conductivities)
                        totalGrad[kvp.Key] += kvp.Value;
                }

                // 4d) Regularization term J_reg and grad ∇J_reg (Eq. 2.1.27/2.1.28)
                double regTerm = _regularizer.EvaluateTerm(mesh, sigma);
                var regGrad = _regularizer.EvaluateGradient(mesh, sigma);
                Debug.WriteLine($"Regularization R = {regTerm:0.#####}");

                // 4h) Total gradient ∇J = ∇J_data + ∇R  (Eq. 2.1.31)
                var totalGradDict = totalGrad.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + _regularizationWeight * regGrad.GetConductivity(kvp.Key)
                );

                // Normalize gradient
                foreach (var kvp in totalGradDict)
                    totalGradDict[kvp.Key] = kvp.Value / simulatedMeasurements.Count;

                var grad = new ConductivityDistribution(totalGradDict);

                Debug.WriteLine("Gradient ∇J computed.");

                // 4i) Apply optimization step
                var newConductivityDistribution = _numericOptimizer.OptimizationStep(mesh.ConductivityDistribution, grad, stepSize);

                mesh.SetConductivityDistribution(newConductivityDistribution);

                // Compute total misfit
                double Jtotal = 0;
                for (int exc = 0; exc < electrodeCount; exc++)
                {
                    // Clear electrode status
                    foreach (var el in electrodes)
                    {
                        el.Current = 0.0;
                        el.IsExcitation = false;
                        el.IsGround = false;
                        el.Potential = 0.0;
                    }

                    // Set new electrode setup
                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].Current = excitationAmplitude;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].Current = -excitationAmplitude;

                    var phiNew = _differentialEquationSolver.Solve(mesh, bc, null);
                    double[] dSimNew = mesh.GetElectrodePotentials();
                    double[] dObs = simulatedMeasurements[exc];
                    Jtotal += _errorMetric.Evaluate(mesh, dObs, dSimNew);
                }
                Debug.WriteLine($"Iteration {iter}: total misfit = {Jtotal}");

                errors.Add(Jtotal);

                if (Math.Abs(prevJ - Jtotal) < tolerance) 
                    break;
                
                prevJ = Jtotal;
            }

            Debug.WriteLine("Erorrs during iteration:");
            // print errors
            for (int i = 0; i < errors.Count; i++)
                if (i % 5 == 0)
                    Debug.WriteLine($"It[{i}]: {errors[i]:F6} ");
                else Debug.Write($"It[{i}]: {errors[i]:F6}\t");

            Debug.WriteLine("");

            // 5) Update mesh ConductivityDistribution and return
            foreach (var el in elements)
                el.Conductivity = sigma.GetConductivity(el.Id);

            return mesh;
        }

        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude)
        {
            List<double[]> measurements = [];

            FEMMesh deepCopy = (FEMMesh)mesh.DeepCopy();
            var electrodes = deepCopy.GetElectrodes().ToList();
            int electrodeCount = electrodes.Count();

            for (int i = 0; i < electrodeCount; i++)
            {
                // Clear electrode status
                foreach(var el in electrodes)
                {
                    el.Current = 0.0;
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.Potential = 0.0;
                }

                // Set new electrode setup
                electrodes[i % electrodeCount].IsExcitation = true;
                electrodes[i % electrodeCount].Current = excitationAmplitude;
                electrodes[(i + 1) % electrodeCount].IsGround = true;
                electrodes[(i + 1) % electrodeCount].Current = -excitationAmplitude;

                FEMMesh result = SolveFemForward(deepCopy);

                measurements.Add(result.GetElectrodePotentials());
            }

            return measurements;
        }

        #endregion

        /// <summary>
        ///     Performs a forward solve on a graph representation of the mesh.
        ///     The finite element mesh is converted to a resistor network and
        ///     the discrete Laplace equation with Complete Electrode Model
        ///     boundary conditions is solved.
        /// </summary>
        /// <param name="mesh">Mesh whose potentials are computed.</param>
        /// <returns>The same mesh populated with the solved potentials.</returns>
        public FEMMesh SolveGraphForward(FEMMesh mesh)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Cannot perform graph based forward solve, differential equation solver is not specified!");

            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            BoundaryCondition bc = new FEMBoundaryCondition(electrodes);
            var pd = _differentialEquationSolver.Solve(mesh, bc, null);
            mesh.SetPotentialDistribution(pd);
            return mesh;
        }

        /// <summary>
        ///     Executes one gradient-descent step for the graph-based inverse
        ///     problem.  The adjoint field <c>μ</c> is obtained from the
        ///     residual <c>d<sub>meas</sub> − d<sub>sim</sub></c> and used to
        ///     update edge conductances via <c>(∇φ·∇μ)</c> on the graph.
        /// </summary>
        /// <param name="mesh">Mesh whose conductivities are updated.</param>
        /// <param name="measurement">Measured electrode potentials.</param>
        /// <param name="boundaryCondition">Applied current pattern.</param>
        /// <param name="stepSize">Currently unused step-size parameter.</param>
        /// <returns>Reconstruction result with updated conductivity field.</returns>
        public ReconstructionResult InverseSolveStepGraph(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize)
        {
            if (_differentialEquationSolver is not GraphSolver graphSolver || _numericSolver == null || _errorMetric == null)
                throw new NullReferenceException("Graph solver path not initialized.");

            PotentialDistribution phi = _differentialEquationSolver.Solve(mesh, boundaryCondition, null);
            var dSim = mesh.GetElectrodePotentials();
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, measurement, dSim);
            var adjComplex = adjSrc.Select(x => new Complex(x, 0.0)).ToArray();
            PotentialDistribution mu = graphSolver.SolveAdjoint(mesh, _numericSolver, adjComplex);
            ConductivityDistribution original = mesh.GetConductivityDistribution();
            ConductivityDistribution sigma = graphSolver.InverseSolve(mesh, boundaryCondition, adjComplex);
            mesh.SetConductivityDistribution(sigma);

            throw new NotImplementedException();
            //return new ReconstructionResult(mesh, phi, mu, original, original, sigma);
        }

        public ReconstructionFrame InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize)
        {
            FEMMesh deepCopy = (FEMMesh)mesh.DeepCopy();
            ConductivityDistribution originalConductivityDistribution = deepCopy.ConductivityDistribution;

            // Initialize inverse solver
            _differentialEquationSolver = DifferentialEquationSolverFactory.Create(mesh, DifferentialEquationSolver.FiniteElementMethod, NumericSolverFactory.Create(NumericSolver.SVD));
            _errorMetric = ErrorMetricFactory.Create(ErrorMetric.L2);
            _regularizer = RegularisationFactory.Create(RegularizationTechnique.ZeroOrderTikhonov, mesh.DeepCopy(), 0.0);
            _numericOptimizer = NumericOptimizerFactory.Create(NumericOptimizer.GradientBased, ConductivityDistributionFactory.CreateRandom(mesh));

            // Initialize conductivity (σ^{(0)}) based on user selection
            ConductivityDistribution sigma0 = ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType);
            mesh.SetConductivityDistribution(sigma0);

            // Create new boundary conditions that will be fed to the Finite Element Solver
            List<FEMElectrode> electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var bc = new FEMBoundaryCondition(electrodes);
            var electrodeCount = electrodes.Count;

            // Forward solve  (thesis Eq. 1.1.16)
            PotentialDistribution phi = _differentialEquationSolver.Solve(mesh, bc, null);
            Debug.WriteLine("Forward φ computed.");

            // Extract simulated boundary data d_sim
            double[] dSim = mesh.GetElectrodePotentials();
            double[] dObs = measurement;

            // Build adjoint source s = EvaluateAdjointSource (L2: residual; W2: Kantorovich φ) 
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, dObs, dSim);

            Complex[] adjointSource = new Complex[adjSrc.Length];
            for (int i = 0; i < adjSrc.Length; i++)
                adjointSource[i] = adjSrc[i];

            // wrap into a PotentialDistribution on electrodes
            var srcDist = new PotentialDistribution(Enumerable.Range(0, adjSrc.Length).ToDictionary(i => electrodes[i].Id, i => adjSrc[i]));

            // 4f) Adjoint solve μ: same forward‐solver but feed in adjSrc as boundary currents
            PotentialDistribution mu = _differentialEquationSolver.Solve(mesh, new FEMBoundaryCondition(electrodes, srcDist), adjointSource);
            Debug.WriteLine("Adjoint μ computed.");

            // 4g) Compute gradient ∇J_data = ∇μ·∇φ elementwise  (thesis Eq. 2.1.20)
            var dataGrad = new ConductivityDistribution(
                mesh.GetElements().Cast<FEMElement>().ToDictionary(
                    el => el.Id,
                    el => {
                        // compute ∇φ, ∇μ on this element
                        var gPhi = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi).GetVector(el.Id);
                        var gMu = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu).GetVector(el.Id);
                        return (gMu.X * gPhi.X + gMu.Y * gPhi.Y) * el.Area;
                    }
                )
            );

            int elementCount = mesh.GetElements().Count();
            Dictionary<int, double> totalGrad = new();
            for (int i = 0; i < elementCount; i++)
                totalGrad.Add(i, 0.0);

            foreach (var kvp in dataGrad.Conductivities)
                totalGrad[kvp.Key] += kvp.Value;            

            // 4d) Regularization term J_reg and grad ∇J_reg (Eq. 2.1.27/2.1.28)
            double regTerm = _regularizer.EvaluateTerm(mesh, sigma0);
            ConductivityDistribution regGrad = _regularizer.EvaluateGradient(mesh, sigma0);
            Debug.WriteLine($"Regularization R = {regTerm:0.#####}");

            // 4h) Total gradient ∇J = ∇J_data + ∇R  (Eq. 2.1.31)
            var totalGradDict = totalGrad.ToDictionary(kvp => kvp.Key, kvp => kvp.Value + regGrad.GetConductivity(kvp.Key));

            ConductivityDistribution grad = new ConductivityDistribution(totalGradDict);

            Debug.WriteLine("Gradient ∇J computed.");

            // 4i) Apply optimization step
            mesh.SetConductivityDistribution(_numericOptimizer.OptimizationStep(mesh.ConductivityDistribution, grad, stepSize));

            return new ReconstructionFrame(grad, phi, mu, regGrad);
        }

        // --- Persistence ---
        public void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters)
            => _reconstructionRepository.SaveReconstruction(frames, name, parameters);

        public IEnumerable<ReconstructionInfo> GetReconstructions()
            => _reconstructionRepository.GetReconstructions();

        public List<ReconstructionResult> LoadReconstruction(string filePath)
            => _reconstructionRepository.LoadReconstruction(filePath);
    }
}

