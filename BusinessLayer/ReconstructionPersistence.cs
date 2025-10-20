using DataAccessLayer;
using System.Diagnostics;
using System.Numerics;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Models;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;
using Utility.Classes.Reconstruction.DESolvers;

namespace BusinessLayer
{
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        private readonly IDAQRepository _daqRepository;
        private readonly IReconstructionRepository _reconstructionRepository;

        private InverseModel? _inverseModel = null;
        
        private IDiscretization? _discretization = null;
        private INumericSolver? _numericSolver = null;
        private IDifferentialEquationSolver? _differentialEquationSolver = null;
        private IRegularizer? _regularizer = null;
        private IErrorMetric? _errorMetric = null;
        private INumericOptimizer? _numericOptimizer = null;

        private double _gradientStepSize = 0.001;
        private double _regularizationWeight = 0.001;
        private InitialDistributionTypes _initialDistributionType = InitialDistributionTypes.SlightlyDiffering;
        private bool _useOmpParallelization = false;
        private bool _useCudaParallelization = false;
        private NumericSolver _numericSolverChoice = NumericSolver.LU;
        private ErrorMetric _errorMetricChoice = ErrorMetric.L2;

        private ConductivityDistribution? _originalSigma = null;
        private ConductivityDistribution? _initialSigma = null;

        private bool _initialized = false;

        private DrivePattern _drivePattern = DrivePattern.Adjecent;

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

        public void SetConductivityDistributions(ConductivityDistribution original, ConductivityDistribution initial)
        {
            _originalSigma = original;
            _initialSigma = initial;
        }

        public void InitializeReconstruction(IDiscretization discretization, EITReconstructionParameters parameters, bool reinit)
        {
            if(!_initialized || reinit)
            {
                _discretization = discretization;

                _numericSolverChoice = parameters.NumericSolver;
                _numericSolver = NumericSolverFactory.Create(_numericSolverChoice);
                _useOmpParallelization = parameters.UseOmpParallelization;
                _useCudaParallelization = parameters.UseCudaAcceleration;

                if (parameters.DifferentialEquationSolver == DifferentialEquationSolver.FEM)
                {
                    Workspace.AddLogMessage("Reconstruction", _useOmpParallelization
                        ? "Using OMP-accelerated finite element assembly."
                        : "Using standard finite element assembly.");
                }
                else if (parameters.DifferentialEquationSolver == DifferentialEquationSolver.LBM)
                {
                    Workspace.AddLogMessage("Reconstruction", _useCudaParallelization
                        ? "Using CUDA-accelerated Lattice Boltzmann solver."
                        : "Using standard Lattice Boltzmann solver.");
                }

                _differentialEquationSolver = DifferentialEquationSolverFactory.Create(discretization,
                                                                                      parameters.DifferentialEquationSolver,
                                                                                      _numericSolver,
                                                                                      _useOmpParallelization,
                                                                                      _useCudaParallelization);
                _regularizer = RegularisationFactory.Create(parameters.RegularizationTechnique, _discretization);
                _errorMetricChoice = parameters.ErrorMetric;
                _errorMetric = ErrorMetricFactory.Create(_errorMetricChoice);
                _initialDistributionType = parameters.InitialDistributionType;
                var initSigma = _initialSigma ?? ConductivityDistributionFactory.CreateInitialDistribution(discretization, _initialDistributionType);
                _numericOptimizer = NumericOptimizerFactory.Create(parameters.NumericOptimizer, initSigma);
                _drivePattern = parameters.DrivePattern;

                _inverseModel = InverseModelFactory.Create(_discretization, _numericOptimizer, _regularizer, _errorMetric, _differentialEquationSolver);

                _initialized = true;
            }
        }

        public ReconstructionFrame Step(double[] measurement, BoundaryCondition boundaryCondition, double gradientStepSize, double redularizationStepSize)
        {
            _regularizationWeight = redularizationStepSize;

            if (_numericOptimizer == null)
                throw new NullReferenceException("Numeric optimizer is null, check calling code!");

            if (_discretization is FEMMesh femMesh)
            {
                FEMBoundaryCondition bc = boundaryCondition as FEMBoundaryCondition ?? throw new ArgumentException("Cannot convert boundary condition to FEM boundary condition, check calling code!");

                var frame = InverseSolveStepFem(femMesh, bc, measurement, gradientStepSize);

                var totalGradDict = frame.ConductivityGradient.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + redularizationStepSize * frame.CalculatedRegularization.GetConductivity(kvp.Key));
                var totalGrad = new ConductivityDistribution(totalGradDict);

                var sigma = femMesh.GetConductivityDistribution();
                var updated = _numericOptimizer.OptimizationStep(sigma, totalGrad, gradientStepSize);
                femMesh.SetConductivityDistribution(updated);

                return new ReconstructionFrame(totalGrad,
                                              frame.CalculatedPotentialDistribution,
                                              frame.CalculatedAdjointDistribution,
                                              frame.CalculatedRegularization);
            }
            else if (_discretization is LBMGrid lbmGrid)
            {
                LBMBoundaryCondition bc = boundaryCondition as LBMBoundaryCondition ?? throw new ArgumentException("Cannot convert boundary condition to LBM boundary condition, check calling code!");

                var frame = InverseSolveStepLbm(lbmGrid, bc, measurement);

                var totalGradDict = frame.ConductivityGradient.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + redularizationStepSize * frame.CalculatedRegularization.GetConductivity(kvp.Key));
                var totalGrad = new ConductivityDistribution(totalGradDict);

                var sigma = lbmGrid.GetConductivityDistribution();
                var updated = _numericOptimizer.OptimizationStep(sigma, totalGrad, gradientStepSize);
                lbmGrid.SetConductivityDistribution(updated);

                return new ReconstructionFrame(totalGrad,
                                              frame.CalculatedPotentialDistribution,
                                              frame.CalculatedAdjointDistribution,
                                              frame.CalculatedRegularization);
            }
            else throw new ArgumentOutOfRangeException();
        }

        public void Run(int maxIterationCount, double gradientStepSize, double redularizationStepSize)
        {
            if (!_initialized || _discretization == null)
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
                if (_discretization is FEMMesh femMesh)
                    return RunFemReconstruction(femMesh, maxIterationCount);
                else if (_discretization is LBMGrid lbmGrid)
                    return RunLbmReconstruction(lbmGrid, maxIterationCount);
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
            if (_discretization is not FEMMesh mesh)
                throw new TypeInitializationException("Mesh should be of type FEMMesh to use FEM solver!", new Exception("Invalid type in solver!"));

            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var boundaryConditions = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(boundaryConditions);

            return _differentialEquationSolver.Solve(mesh, boundaryConditions, null);
        }

        public PotentialDistribution ForwardSolveStepLbm()
        {
            if (_discretization is not LBMGrid lbmGrid)
                throw new TypeInitializationException("Mesh should be of type LBMGrid to use LBM solver!", new Exception("Invalid type in solver!"));

            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            Workspace.UpdateCurrentGlobalLbmElectrodes(lbmGrid);
            var electrodes = Workspace.GetCurrentGlobalLbmElectrodes();
            var boundaryConditions = new LBMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(boundaryConditions);

            return _differentialEquationSolver.Solve(lbmGrid, boundaryConditions, null);
        }

        public PotentialDistribution ForwardSolveStepLbmCuda()
        {
            if (_discretization is not LBMGrid lbmGrid)
                throw new TypeInitializationException("Mesh should be of type LBMGrid to use LBM solver!", new Exception("Invalid type in solver!"));

            if (_differentialEquationSolver is not LatticeBoltzmannDESolver lbmSolver)
                throw new InvalidOperationException("CUDA forward solve requested, but LBM solver is not initialised.");

            Workspace.UpdateCurrentGlobalLbmElectrodes(lbmGrid);
            var electrodes = Workspace.GetCurrentGlobalLbmElectrodes();
            var boundaryConditions = new LBMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(boundaryConditions);

            return lbmSolver.CUDASolveForward(lbmGrid, boundaryConditions);
        }

        public ReconstructionFrame InverseSolveStepFem(FEMMesh mesh, FEMBoundaryCondition bc, double[] currentMeasurement, double gradientStepSize)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            if (_errorMetric == null)
                throw new NullReferenceException("Error metric is null, check calling code!");

            if (_regularizer == null)
                throw new NullReferenceException("Regularizer is null, check calling code!");

            return ComputeFemInverseStep(mesh,
                                         bc,
                                         currentMeasurement,
                                         gradientStepSize,
                                         _differentialEquationSolver,
                                         _errorMetric,
                                         _regularizer,
                                         updateWorkspace: true);
        }

        public ReconstructionFrame InverseSolveStepLbm(LBMGrid mesh, LBMBoundaryCondition bc, double[] currentMeasurement)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            if (_errorMetric == null)
                throw new NullReferenceException("Error metric is null, check calling code!");

            Workspace.UpdateCurrentGlobalLbmElectrodes(mesh);
            Workspace.UpdateCurrentGlobalLbmElements(mesh);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(bc);
            var electrodes = Workspace.GetCurrentGlobalLbmElectrodes();
            var elements = Workspace.GetCurrentGlobalLbmElements();

            // Solve Forward to extract simulated potentials
            var lbmSolver = _differentialEquationSolver as LatticeBoltzmannDESolver;

            PotentialDistribution phi = _useCudaParallelization && lbmSolver != null
                ? lbmSolver.CUDASolveForward(mesh, bc)
                : _differentialEquationSolver.Solve(mesh, bc, null);

            // Extract simulated potentials
            double[] simulatedPotentials = mesh.GetElectrodePotentials();

            double currentError = _errorMetric.Evaluate(mesh, currentMeasurement, simulatedPotentials);

            // Error metric based gradeint expression
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, currentMeasurement, simulatedPotentials);

            Complex[] adjointSource = new Complex[adjSrc.Length];
            for (int k = 0; k < adjSrc.Length; k++)
                adjointSource[k] = adjSrc[k];

            var adjointBoundaryCondition = new LBMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(adjointBoundaryCondition);
            PotentialDistribution mu = _useCudaParallelization && lbmSolver != null
                ? lbmSolver.CUDASolveAdjoint(mesh, adjointBoundaryCondition, adjointSource)
                : _differentialEquationSolver.Solve(mesh, adjointBoundaryCondition, adjointSource);

            var phiGradientField = _useCudaParallelization
                ? LatticeBoltzmannOperators.CalculateGradientCuda(mesh, phi)
                : LatticeBoltzmannOperators.CalculateGradient(mesh, phi);
            var muGradientField = _useCudaParallelization
                ? LatticeBoltzmannOperators.CalculateGradientCuda(mesh, mu)
                : LatticeBoltzmannOperators.CalculateGradient(mesh, mu);

            ConductivityDistribution dataGrad = new ConductivityDistribution(
                elements.ToDictionary(
                    el => el.Id,
                    el => {
                        var gPhi = phiGradientField.GetVector(el.Id);
                        var gMu = muGradientField.GetVector(el.Id);
                        return -(gMu.X * gPhi.X + gMu.Y * gPhi.Y);
                    }
                )
            );

            return new ReconstructionFrame(dataGrad, phi, mu, new ConductivityDistribution([]));
        }

        private ReconstructionFrame ComputeFemInverseStep(FEMMesh mesh,
                                                          FEMBoundaryCondition bc,
                                                          double[] currentMeasurement,
                                                          double gradientStepSize,
                                                          IDifferentialEquationSolver solver,
                                                          IErrorMetric errorMetric,
                                                          IRegularizer regularizer,
                                                          bool updateWorkspace)
        {
            if (solver == null)
                throw new ArgumentNullException(nameof(solver));
            if (errorMetric == null)
                throw new ArgumentNullException(nameof(errorMetric));
            if (regularizer == null)
                throw new ArgumentNullException(nameof(regularizer));

            _ = gradientStepSize; // Currently unused but preserved for API compatibility.

            List<FEMElectrode> electrodes;
            List<FEMElement> elements;

            if (updateWorkspace)
            {
                Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
                Workspace.UpdateCurrentGlobalFemElements(mesh);
                Workspace.SetCurrentGlobalFemBoundaryCondition(bc);

                electrodes = Workspace.GetCurrentGlobalFemElectrodes();
                elements = Workspace.GetCurrentGlobalFemElements();
            }
            else
            {
                electrodes = new List<FEMElectrode>(bc.GetElectrodes());
                elements = mesh.GetElements().Cast<FEMElement>().ToList();
            }

            PotentialDistribution phi = solver.Solve(mesh, bc, null);
            double[] simulatedPotentials = mesh.GetElectrodePotentials();

            var adjSrc = errorMetric.EvaluateAdjointSource(mesh, currentMeasurement, simulatedPotentials);
            Complex[] adjointSource = new Complex[adjSrc.Length];
            for (int k = 0; k < adjSrc.Length; k++)
                adjointSource[k] = adjSrc[k];

            var adjointBoundaryCondition = new FEMBoundaryCondition(new List<FEMElectrode>(electrodes));
            if (updateWorkspace)
                Workspace.SetCurrentGlobalFemBoundaryCondition(adjointBoundaryCondition);

            PotentialDistribution mu = solver.Solve(mesh, adjointBoundaryCondition, adjointSource);

            var phiGradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi);
            var muGradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu);

            ConductivityDistribution dataGrad = new ConductivityDistribution(
                elements.ToDictionary(
                    el => el.Id,
                    el =>
                    {
                        var gPhi = phiGradient.GetVector(el.Id);
                        var gMu = muGradient.GetVector(el.Id);
                        return -(gMu.X * gPhi.X + gMu.Y * gPhi.Y) * el.Area;
                    })
            );

            ConductivityDistribution sigma = mesh.GetConductivityDistribution();

            _ = regularizer.EvaluateTerm(mesh, sigma);
            ConductivityDistribution regularization = regularizer.EvaluateGradient(mesh, sigma);

            return new ReconstructionFrame(dataGrad, phi, mu, regularization);
        }

        public ReconstructionFrame InverseSolveStepLbmCuda(LBMGrid mesh, LBMBoundaryCondition bc, double[] currentMeasurement)
        {
            bool previous = _useCudaParallelization;
            _useCudaParallelization = true;
            try
            {
                return InverseSolveStepLbm(mesh, bc, currentMeasurement);
            }
            finally
            {
                _useCudaParallelization = previous;
            }
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

            ConductivityDistribution originalSigma = _originalSigma ?? mesh.DeepCopy().GetConductivityDistribution();
            List<double[]> measurementFrames;
            if (_originalSigma != null)
            {
                FEMMesh measMesh = (FEMMesh)mesh.DeepCopy();
                measMesh.SetConductivityDistribution(originalSigma);
                measurementFrames = SimulateFemMeasurements(measMesh, 1.0, _drivePattern);
            }
            else
            {
                measurementFrames = SimulateFemMeasurements(mesh, 1.0, _drivePattern);
            }

            ConductivityDistribution initialSigma = _initialSigma ?? ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType);
            mesh.SetConductivityDistribution(initialSigma);

            // Cache electrode and element information for repeated use.
            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            Workspace.UpdateCurrentGlobalFemElements(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var elements = Workspace.GetCurrentGlobalFemElements();
            int electrodeCount = electrodes.Count;

            // Container that stores intermediate frames for later inspection.
            List<ReconstructionFrame> frames = [];

            // --- Iterative reconstruction loop -----------------------------------------
            for (int iter = 0; iter < maxIterationCount && !_stopRequested; iter++)
            {
                // Initialise an accumulator for the gradient contributions of
                // all electrode pairs.
                var totalGrad = elements.ToDictionary(el => el.Id, _ => 0.0);

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
                        el.IsMeasuring = true;
                        el.Potential = 0.0;
                    }

                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].IsMeasuring = false;
                    electrodes[exc % electrodeCount].Current = 1.0;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].IsMeasuring = false;
                    electrodes[(exc + 1) % electrodeCount].Current = -1.0;

                    // Boundary condition reflecting the just configured
                    // electrode setup.
                    var bc = new FEMBoundaryCondition(electrodes);
                    Workspace.SetCurrentGlobalFemBoundaryCondition(bc);

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
        private ReconstructionResult RunLbmReconstruction(LBMGrid mesh, int maxIterationCount)
        {
            if (_errorMetric == null)
                throw new NullReferenceException("Error metric not initialised.");

            ConductivityDistribution originalSigma = _originalSigma ?? ((LBMGrid)mesh.DeepCopy()).GetConductivityDistribution();
            EITMeasurement measurementFrames;
            if (_originalSigma != null)
            {
                LBMGrid measMesh = (LBMGrid)mesh.DeepCopy();
                measMesh.SetConductivityDistribution(originalSigma);
                measurementFrames = SimulateLbmMeasurements(measMesh, 1.0, _drivePattern);
            }
            else
            {
                measurementFrames = SimulateLbmMeasurements(mesh, 1.0, _drivePattern);
            }

            ConductivityDistribution initialSigma = _initialSigma ?? mesh.GetConductivityDistribution();
            mesh.SetConductivityDistribution(initialSigma);

            Workspace.UpdateCurrentGlobalLbmElectrodes(mesh);
            Workspace.UpdateCurrentGlobalLbmElements(mesh);
            var electrodes = Workspace.GetCurrentGlobalLbmElectrodes();
            int electrodeCount = electrodes.Count;

            var elements = Workspace.GetCurrentGlobalLbmElements();

            List<ReconstructionFrame> frames = [];

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
                        el.IsMeasuring = true;
                        el.Potential = 0.0;
                    }

                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].IsMeasuring = false;
                    electrodes[exc % electrodeCount].Current = 1.0;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].IsMeasuring = false;
                    electrodes[(exc + 1) % electrodeCount].Current = -1.0;

                    var bc = new LBMBoundaryCondition(electrodes);
                    Workspace.SetCurrentGlobalLbmBoundaryCondition(bc);
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
            if(_discretization is not FEMMesh mesh)
                throw new TypeInitializationException("Mesh should be of type FEMMesh to use FEM solver!", new Exception("Invalid type in solver!"));

            if (_regularizer == null)
                throw new NullReferenceException("Regularizer is null, check calling code!");

            if (_numericOptimizer == null)
                throw new NullReferenceException("Numeric optimizer is null, check calling code!");

            _regularizationWeight = redularizationStepSize;

            ConductivityDistribution originalConductivityDistribution = _originalSigma ?? mesh.DeepCopy().GetConductivityDistribution();
            List<double[]> simulatedMeasurements;
            if (_originalSigma != null)
            {
                FEMMesh measMesh = (FEMMesh)mesh.DeepCopy();
                measMesh.SetConductivityDistribution(originalConductivityDistribution);
                simulatedMeasurements = SimulateFemMeasurements(measMesh, excitationAmplitude, _drivePattern);
            }
            else
                simulatedMeasurements = SimulateFemMeasurements(mesh, excitationAmplitude, _drivePattern);
            
            ConductivityDistribution initialConductivityDistribution = _initialSigma ?? ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType);
            mesh.SetConductivityDistribution(initialConductivityDistribution);

            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            Workspace.UpdateCurrentGlobalFemElements(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var bc = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(bc);
            int electrodeCount = electrodes.Count;

            var elements = Workspace.GetCurrentGlobalFemElements();
            int elementCount = elements.Count;

            // 4) Iterative loop
            double prevJ = double.PositiveInfinity;
            List<double> errors = [];
            List<ReconstructionFrame> frames = [];

            for (int iter = 0; iter < maxIterationCount; iter++)
            {
                Debug.WriteLine($"\n=== Inverse iteration {iter} ===");

                Dictionary<int, double> totalGrad = [];
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
                        el.IsMeasuring = true;
                        el.Potential = 0.0;
                    }

                    // Set new electrode setup
                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].IsMeasuring = false;
                    electrodes[exc % electrodeCount].Current = excitationAmplitude;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].IsMeasuring = false;
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
            if (_discretization is not LBMGrid lbmGrid)
                throw new TypeInitializationException("Mesh should be of type LBMGrid to use LBM solver!", new Exception("Invalid type in solver!"));

            if (_regularizer == null)
                throw new NullReferenceException("Regularizer is null, check calling code!");

            _gradientStepSize = gradientStepSize;
            _regularizationWeight = redularizationStepSize;

            return RunLbmReconstruction(lbmGrid, maxIterationCount);
        }

        public ReconstructionResult InverseSolveLbmCuda(int maxIterationCount,
                                                        double gradientStepSize,
                                                        double redularizationStepSize,
                                                        double excitationAmplitude,
                                                        double tolerance = 1e-6)
        {
            bool previous = _useCudaParallelization;
            _useCudaParallelization = true;
            try
            {
                return InverseSolveLbm(maxIterationCount, gradientStepSize, redularizationStepSize, excitationAmplitude, tolerance);
            }
            finally
            {
                _useCudaParallelization = previous;
            }
        }

        private double CalculateTotalMisiftFem(FEMMesh mesh, List<double[]> simulatedMeasurements, FEMBoundaryCondition bc, double excitationAmplitude)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            if (_errorMetric == null)
                throw new NullReferenceException("Error metric is null, check calling code!");

            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            Workspace.SetCurrentGlobalFemBoundaryCondition(bc);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
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
                    el.IsMeasuring = true;
                    el.Potential = 0.0;
                }

                // Set new electrode setup
                electrodes[exc % electrodeCount].IsExcitation = true;
                electrodes[exc % electrodeCount].IsMeasuring = false;
                electrodes[exc % electrodeCount].Current = excitationAmplitude;
                electrodes[(exc + 1) % electrodeCount].IsGround = true;
                electrodes[(exc + 1) % electrodeCount].IsMeasuring = false;
                electrodes[(exc + 1) % electrodeCount].Current = -excitationAmplitude;

                _ = _differentialEquationSolver.Solve(mesh, bc, null);
                double[] dSimNew = mesh.GetElectrodePotentials();
                double[] dObs = simulatedMeasurements[exc];
                Jtotal += _errorMetric.Evaluate(mesh, dObs, dSimNew);
            }

            return Jtotal;
        }


        #region Lattice Boltzmann Reconstruction

        private ReconstructionFrame LbmSolveStep(LBMGrid mesh, LBMBoundaryCondition bc, double[] currentMeasurement)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Cannot perform solve step, DE solver is null.");

            if (_errorMetric == null)
                throw new NullReferenceException("Cannot perform solve step Error Metric is null.");

            Workspace.UpdateCurrentGlobalLbmElectrodes(mesh);
            Workspace.UpdateCurrentGlobalLbmElements(mesh);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(bc);
            var electrodes = Workspace.GetCurrentGlobalLbmElectrodes();
            var elements = Workspace.GetCurrentGlobalLbmElements();

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

            var adjointBoundaryCondition = new LBMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(adjointBoundaryCondition);
            PotentialDistribution mu = _differentialEquationSolver.Solve(mesh, adjointBoundaryCondition, adjointSource);

            var phiGradientField = _useCudaParallelization
                ? LatticeBoltzmannOperators.CalculateGradientCuda(mesh, phi)
                : LatticeBoltzmannOperators.CalculateGradient(mesh, phi);
            var muGradientField = _useCudaParallelization
                ? LatticeBoltzmannOperators.CalculateGradientCuda(mesh, mu)
                : LatticeBoltzmannOperators.CalculateGradient(mesh, mu);

            ConductivityDistribution dataGrad = new ConductivityDistribution(
                elements.ToDictionary(
                    el => el.Id,
                    el => {
                        var gPhi = phiGradientField.GetVector(el.Id);
                        var gMu = muGradientField.GetVector(el.Id);
                        return (gMu.X * gPhi.X + gMu.Y * gPhi.Y);
                    }
                )
            );

            return new ReconstructionFrame(dataGrad, phi, mu, new ConductivityDistribution([]));
        }

        public ReconstructionResult SolveLbmInverse(int maxIterationCount)
        {
            double stepSize = _gradientStepSize;
            LBMGrid? lbmGrid = (_discretization as LBMGrid);

            if (_inverseModel == null || _discretization == null || lbmGrid == null || _differentialEquationSolver == null || _errorMetric == null)
                throw new NullReferenceException();

            ConductivityDistribution originalConductivityDistribution = _originalSigma ?? ((LBMGrid)lbmGrid.DeepCopy()).GetConductivityDistribution();
            lbmGrid = (LBMGrid)lbmGrid.DeepCopy();

            Workspace.UpdateCurrentGlobalLbmElectrodes(lbmGrid);
            Workspace.UpdateCurrentGlobalLbmElements(lbmGrid);
            var electrodes = Workspace.GetCurrentGlobalLbmElectrodes();

            LBMBoundaryCondition bc = new(electrodes);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(bc);

            EITMeasurement measurementFrames;
            if (_originalSigma != null)
            {
                LBMGrid measMesh = (LBMGrid)lbmGrid.DeepCopy();
                measMesh.SetConductivityDistribution(originalConductivityDistribution);
                measurementFrames = SimulateLbmMeasurements(measMesh, 1.0, _drivePattern);
            }
            else
            {
                measurementFrames = SimulateLbmMeasurements(lbmGrid, 1.0, _drivePattern);
            }

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

                    var frame = LbmSolveStep(lbmGrid, bc, currentMeasurement);

                    // --- Set new conductivities ---
                    double step = stepSize;  
                    var sigma = lbmGrid.GetConductivityDistribution();

                    var newSigmaDict = sigma.Conductivities.ToDictionary(
                        kvp => kvp.Key,
                        // Clamp conductivites which got below 0
                        kvp => ((kvp.Value - step * frame.ConductivityGradient.GetConductivity(kvp.Key)) < 0.0) ?
                                                                1e-1 : kvp.Value - step * frame.ConductivityGradient.GetConductivity(kvp.Key)
                    );

                    sigma = new ConductivityDistribution(newSigmaDict);
                    lbmGrid.SetConductivityDistribution(sigma);

                    // Add partial results
                    frames.Add(frame);
                }
            }

            ConductivityDistribution reconstructedConductivityDistribution = lbmGrid.GetConductivityDistribution();

            var initialConductivityDistribution = _initialSigma ?? lbmGrid.GetConductivityDistribution();
            return new ReconstructionResult((LBMGrid)lbmGrid,
                                            originalConductivityDistribution,
                                            initialConductivityDistribution,
                                            reconstructedConductivityDistribution,
                                            frames);
        }

        public EITMeasurement SimulateLbmMeasurements(LBMGrid mesh, double exciationAmplitude, DrivePattern drivePattern)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver was null, check initializiation code!");

            LBMGrid deepCopy = (LBMGrid)mesh.DeepCopy();
            Workspace.UpdateCurrentGlobalLbmElectrodes(deepCopy);
            var electrodes = Workspace.GetCurrentGlobalLbmElectrodes();
            int electrodeCount = electrodes.Count;

            var strategy = DrivePatternStrategyProvider.GetStrategy(drivePattern);
            int cycleLength = Math.Max(1, strategy.GetCycleLength(electrodeCount));

            double[,] measurementFrames = new double[cycleLength, electrodeCount];

            for (int i = 0; i < cycleLength; i++)
            {
                foreach (var el in electrodes)
                {
                    el.IsMeasuring = true;
                    el.IsGround = false;
                    el.IsExcitation = false;
                    el.Potential = 0.0;
                    el.Current = 0.0;
                }

                var (excitationIndex, groundIndex) = strategy.GetElectrodePair(electrodeCount, i);

                electrodes[excitationIndex].IsExcitation = true;
                electrodes[excitationIndex].IsMeasuring = false;
                electrodes[excitationIndex].Current = exciationAmplitude;
                electrodes[groundIndex].IsGround = true;
                electrodes[groundIndex].IsMeasuring = false;
                electrodes[groundIndex].Current = -exciationAmplitude;

                LBMBoundaryCondition boundaryCondition = new LBMBoundaryCondition(electrodes);
                Workspace.SetCurrentGlobalLbmBoundaryCondition(boundaryCondition);

                _ = _differentialEquationSolver.Solve(deepCopy, boundaryCondition, null);

                double[] electrodePotentials = deepCopy.GetElectrodePotentials();

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

            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var boundaryConditions = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(boundaryConditions);

            PotentialDistribution potentialDistribution = _differentialEquationSolver.Solve(mesh, boundaryConditions, null);
            mesh.SetPotentialDistribution(potentialDistribution);

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

            List<double[]> simulatedMeasurements = SimulateFemMeasurements(mesh, excitationAmplitude, _drivePattern);
            
            // 2) Initialize conductivity (σ^{(0)}) based on user selection
            ConductivityDistribution sigma = ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType);
            mesh.SetConductivityDistribution(sigma);

            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            Workspace.UpdateCurrentGlobalFemElements(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var bc = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(bc);
            int electrodeCount = electrodes.Count;

            var elements = Workspace.GetCurrentGlobalFemElements();
            int elementCount = elements.Count;

            // 4) Iterative loop
            double prevJ = double.PositiveInfinity;
            List<double> errors = [];

            // TODO: Add partial results

            for (int iter = 0; iter < maxIterCount; iter++)
            {
                Debug.WriteLine($"\n=== Inverse iteration {iter} ===");

                Dictionary<int, double> totalGrad = [];
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
                        el.IsMeasuring = true;
                        el.Potential = 0.0;
                    }

                    // Set new electrode setup
                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].IsMeasuring = false;
                    electrodes[exc % electrodeCount].Current = excitationAmplitude;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].IsMeasuring = false;
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
                    var adjointBoundaryCondition = new FEMBoundaryCondition(electrodes, srcDist);
                    Workspace.SetCurrentGlobalFemBoundaryCondition(adjointBoundaryCondition);
                    var mu = _differentialEquationSolver.Solve(mesh, adjointBoundaryCondition, adjointSource);
                    Debug.WriteLine("Adjoint μ computed.");

                    // 4g) Compute gradient ∇J_data = ∇μ·∇φ elementwise  (thesis Eq. 2.1.20)
                    var phiGradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi);
                    var muGradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu);

                    var dataGrad = new ConductivityDistribution(
                        Workspace.GetCurrentGlobalFemElements().ToDictionary(
                            el => el.Id,
                            el => {
                                // compute ∇φ, ∇μ on this element
                                var gPhi = phiGradient.GetVector(el.Id);
                                var gMu = muGradient.GetVector(el.Id);
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
                        el.IsMeasuring = true;
                        el.Potential = 0.0;
                    }

                    // Set new electrode setup
                    electrodes[exc % electrodeCount].IsExcitation = true;
                    electrodes[exc % electrodeCount].IsMeasuring = false;
                    electrodes[exc % electrodeCount].Current = excitationAmplitude;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].IsMeasuring = false;
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

        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude, DrivePattern drivePattern)
        {
            List<double[]> measurements = [];

            FEMMesh deepCopy = (FEMMesh)mesh.DeepCopy();
            var electrodes = deepCopy.GetElectrodes().ToList();
            int electrodeCount = electrodes.Count();

            var strategy = DrivePatternStrategyProvider.GetStrategy(drivePattern);
            int cycleLength = Math.Max(1, strategy.GetCycleLength(electrodeCount));

            for (int i = 0; i < cycleLength; i++)
            {
                // Clear electrode status
                foreach(var el in electrodes)
                {
                    el.Current = 0.0;
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.IsMeasuring = true;
                    el.Potential = 0.0;
                }

                // Set new electrode setup
                var (excitationIndex, groundIndex) = strategy.GetElectrodePair(electrodeCount, i);
                electrodes[excitationIndex].IsExcitation = true;
                electrodes[excitationIndex].IsMeasuring = false;
                electrodes[excitationIndex].Current = excitationAmplitude;
                electrodes[groundIndex].IsGround = true;
                electrodes[groundIndex].IsMeasuring = false;
                electrodes[groundIndex].Current = -excitationAmplitude;

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

            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var bc = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(bc);
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

        // --- Persistence ---
        public void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters)
            => _reconstructionRepository.SaveReconstruction(frames, name, parameters);

        public IEnumerable<ReconstructionInfo> GetReconstructions() => _reconstructionRepository.GetReconstructions();

        public List<ReconstructionResult> LoadReconstruction(string filePath) => _reconstructionRepository.LoadReconstruction(filePath);
    }
}
