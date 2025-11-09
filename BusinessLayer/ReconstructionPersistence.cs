using DataAccessLayer;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Models;
using Utility.Classes.Reconstruction;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes.ReconstructionParameters;
using Utility.Exports;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;
using Utility.Classes.Reconstruction.DESolvers;

namespace BusinessLayer
{
    /// <summary>
    /// Central orchestration and persistence layer for EIT reconstructions.
    /// - Initializes the forward/adjoint solvers, error metric, regularizer, and optimizer from user parameters.
    /// - Provides single "step" APIs (forward and inverse) for FEM and LBM discretizations.
    /// - Implements full iterative reconstruction loops (foreground and background) for FEM/LBM.
    /// - Bridges workspace state (electrodes, boundary conditions) and manages measurement preparation.
    /// - Persists reconstruction results through the repository.
    ///
    /// This class does not own meshes; it operates on the provided discretizations.
    /// It also supports an optional background task mode with Run()/Stop() controlling the lifecycle.
    /// </summary>
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        // --- Persistence / repositories ---
        private readonly IDAQRepository _daqRepository;
        private readonly IReconstructionRepository _reconstructionRepository;
        private readonly IMeasurementPersistence _measurementPersistence;

        // --- Core components configured during InitializeReconstruction() ---
        private InverseModel? _inverseModel = null;
        private IDiscretization? _discretization = null;
        private INumericSolver? _numericSolver = null;
        private IDifferentialEquationSolver? _differentialEquationSolver = null;
        private IRegularizer? _regularizer = null;
        private IErrorMetric? _errorMetric = null;
        private INumericOptimizer? _numericOptimizer = null;

        // --- User-configurable / session parameters ---
        private double _gradientStepSize = 0.001;           // step size for gradient update in background loops
        private double _regularizationWeight = 0.001;       // weight of regularization contribution
        private InitialDistributionTypes _initialDistributionType = InitialDistributionTypes.SlightlyDiffering;
        private bool _useOmpParallelization = false;        // enable OMP for FEM assembly
        private bool _useCudaParallelization = false;       // enable CUDA in LBM routines
        private bool _usePotentialDifferences = false;      // represent data as sequential potential differences instead of absolute values
        private NumericSolver _numericSolverChoice = NumericSolver.LU;
        private ErrorMetric _errorMetricChoice = ErrorMetric.L2;

        private double _conductivityMinimumBound = 0.1;     // clipping bounds for conductivity fields
        private double _conductivityMaximumBound = 10.0;

        // Optional reference and initial conductivity distributions supplied from UI/test harness
        private ConductivityDistribution? _originalSigma = null;
        private ConductivityDistribution? _initialSigma = null;

        private bool _initialized = false;                  // indicates successful InitializeReconstruction()

        private DrivePattern _drivePattern = DrivePattern.Adjecent; // electrode drive pattern used for simulations

        // --- Background reconstruction bookkeeping ---
        // Holds the running reconstruction task.  The task performs full
        // cycles of the inverse solver until a stop is requested.
        private Task<ReconstructionResult>? _backgroundTask;

        // Flag set by the Stop() method.  When true the background task
        // finishes the current cycle and then returns the accumulated
        // reconstruction result.
        private bool _stopRequested = false;

        public ReconstructionPersistence(IDAQRepository daqRepository,
                                         IReconstructionRepository reconstructionRepository,
                                         IMeasurementPersistence measurementPersistence)
        {
            _daqRepository = daqRepository;
            _reconstructionRepository = reconstructionRepository;
            _measurementPersistence = measurementPersistence;
        }

        /// <summary>
        /// Sets externally provided conductivity fields used as ground truth and initial guess.
        /// Values are clipped to global bounds via <see cref="ConductivityClipper"/>.
        /// If not set, defaults are created during initialization.
        /// </summary>
        public void SetConductivityDistributions(ConductivityDistribution original, ConductivityDistribution initial)
        {
            _originalSigma = ConductivityClipper.Clip(original);
            _initialSigma = ConductivityClipper.Clip(initial);
        }

        /// <summary>
        /// Initializes all components required for reconstruction based on the given discretization and parameters.
        /// Creates the DE solver (FEM/LBM), error metric, regularizer, numeric solver and optimizer.
        /// Updates workspace globals (bounds/clipping) and prepares the inverse model.
        /// </summary>
        /// <param name="discretization">The mesh/grid to reconstruct (FEMMesh or LBMGrid).</param>
        /// <param name="parameters">User configuration for solvers and reconstruction.</param>
        /// <param name="reinit">If true forces re-initialization.</param>
        public void InitializeReconstruction(IDiscretization discretization, EITReconstructionParameters parameters, bool reinit)
        {
            if(!_initialized || reinit)
            {
                _discretization = discretization;

                // --- Numeric linear solver / parallelization choices ---
                _numericSolverChoice = parameters.NumericSolver;
                _numericSolver = NumericSolverFactory.Create(_numericSolverChoice);
                _useOmpParallelization = parameters.UseOmpParallelization;
                _useCudaParallelization = parameters.UseCudaAcceleration;

                // Inform user about assembly/solver mode
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

                // Create differential equation solver according to discretization and requested backend
                _differentialEquationSolver = DifferentialEquationSolverFactory.Create(discretization,
                                                                                      parameters.DifferentialEquationSolver,
                                                                                      _numericSolver,
                                                                                      _useOmpParallelization,
                                                                                      _useCudaParallelization);
                _regularizer = RegularisationFactory.Create(parameters.RegularizationTechnique, _discretization);
                _errorMetricChoice = parameters.ErrorMetric;
                _errorMetric = ErrorMetricFactory.Create(_errorMetricChoice);
                _initialDistributionType = parameters.InitialDistributionType;

                // Initial conductivity distribution; may be provided externally
                var initSigma = _initialSigma ?? ConductivityDistributionFactory.CreateInitialDistribution(discretization, _initialDistributionType);

                // Clamp global conductivity bounds; also clip cached distributions
                _conductivityMinimumBound = parameters.ConductivityMinimumBound;
                _conductivityMaximumBound = parameters.ConductivityMaximumBound;
                ConductivityClipper.UpdateBounds(_conductivityMinimumBound, _conductivityMaximumBound);
                if (_initialSigma != null)
                    _initialSigma = ConductivityClipper.Clip(_initialSigma);
                if (_originalSigma != null)
                    _originalSigma = ConductivityClipper.Clip(_originalSigma);
                initSigma = ConductivityClipper.Clip(initSigma);

                _numericOptimizer = NumericOptimizerFactory.Create(parameters.NumericOptimizer, initSigma);
                _drivePattern = parameters.DrivePattern;
                _usePotentialDifferences = parameters.UsePotentialDifferences;

                // Assemble the high-level inverse model pipeline
                _inverseModel = InverseModelFactory.Create(_discretization, _numericOptimizer, _regularizer, _errorMetric, _differentialEquationSolver);

                _initialized = true;
            }
        }

        /// <summary>
        /// Performs one full inverse update step on the current discretization.
        /// - Runs a forward and adjoint solve to compute the data gradient.
        /// - Adds regularization contribution and updates the conductivity via the optimizer.
        /// </summary>
        /// <param name="measurement">Current measured potentials for the active drive pattern.</param>
        /// <param name="boundaryCondition">Boundary conditions representing the drive electrodes.</param>
        /// <param name="gradientStepSize">Optimizer step size for this update.</param>
        /// <param name="redularizationStepSize">Weight for the regularization gradient.</param>
        /// <returns>A frame capturing the gradient and relevant potential fields.</returns>
        public ReconstructionFrame Step(double[] measurement, BoundaryCondition boundaryCondition, double gradientStepSize, double regularizationStepSize)
        {
            _regularizationWeight = regularizationStepSize;

            if (_numericOptimizer == null)
                throw new NullReferenceException("Numeric optimizer is null, check calling code!");

            // Dispatch by discretization type (FEM vs LBM); compute frame and update sigma
            if (_discretization is FEMMesh femMesh)
            {
                FEMBoundaryCondition bc = boundaryCondition as FEMBoundaryCondition ?? throw new ArgumentException("Cannot convert boundary condition to FEM boundary condition, check calling code!");

                var frame = InverseSolveStepFem(femMesh, bc, measurement, gradientStepSize);

                // Combine data and regularization gradients before optimizer step
                var totalGradDict = frame.ConductivityGradient.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value - regularizationStepSize * frame.CalculatedRegularization.GetConductivity(kvp.Key));
                var totalGrad = new ConductivityDistribution(totalGradDict);

                var sigma = femMesh.GetConductivityDistribution();
                var updated = _numericOptimizer.OptimizationStep(sigma, totalGrad, gradientStepSize);
                updated = ConductivityClipper.Clip(updated);
                femMesh.SetConductivityDistribution(updated);

                return new ReconstructionFrame(totalGrad,
                                              frame.CalculatedPotentialDistribution,
                                              frame.CalculatedAdjointDistribution,
                                              frame.CalculatedRegularization,
                                              frame.MeasuredElectrodeValues,
                                              frame.SimulatedElectrodeValues);
            }
            else if (_discretization is LBMGrid lbmGrid)
            {
                LBMBoundaryCondition bc = boundaryCondition as LBMBoundaryCondition ?? throw new ArgumentException("Cannot convert boundary condition to LBM boundary condition, check calling code!");

                var frame = InverseSolveStepLbm(lbmGrid, bc, measurement);

                var totalGradDict = frame.ConductivityGradient.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value - regularizationStepSize * frame.CalculatedRegularization.GetConductivity(kvp.Key));
                var totalGrad = new ConductivityDistribution(totalGradDict);

                var sigma = lbmGrid.GetConductivityDistribution();
                var updated = _numericOptimizer.OptimizationStep(sigma, totalGrad, gradientStepSize);
                updated = ConductivityClipper.Clip(updated);
                lbmGrid.SetConductivityDistribution(updated);

                return new ReconstructionFrame(totalGrad,
                                              frame.CalculatedPotentialDistribution,
                                              frame.CalculatedAdjointDistribution,
                                              frame.CalculatedRegularization,
                                              frame.MeasuredElectrodeValues,
                                              frame.SimulatedElectrodeValues);
            }
            else throw new ArgumentOutOfRangeException();
        }

        /// <summary>
        /// Starts a background task that performs full reconstruction cycles on the active discretization.
        /// The loop runs until either the maximum iteration count is reached or <see cref="Stop"/> is called.
        /// Stores the user-provided step sizes so the task can update sigma without UI thread participation.
        /// </summary>
        public void Run(int maxIterationCount, double gradientStepSize, double redularizationStepSize)
        {
            if (!_initialized || _discretization == null)
                throw new InvalidOperationException("Reconstruction must be initialised before calling Run().");

            // Store parameters for use in the background loop
            _gradientStepSize = gradientStepSize;
            _regularizationWeight = redularizationStepSize;

            // Reset cancellation request in case this is a new run
            _stopRequested = false;

            // Spawn the background worker that selects the appropriate routine for the discretization type
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

        /// <summary>
        /// Signals the background reconstruction task to finish its current cycle and return.
        /// Waits for completion and returns the final <see cref="ReconstructionResult"/>.
        /// </summary>
        public ReconstructionResult Stop()
        {
            if (_backgroundTask == null)
                throw new InvalidOperationException("Run() must be called before Stop().");

            // Request graceful stop; the loops poll this flag once per iteration
            _stopRequested = true;

            // Await completion and propagate any exceptions directly
            var result = _backgroundTask.GetAwaiter().GetResult();

            // Clear task reference so a new run can be started later
            _backgroundTask = null;

            return result;
        }

        /// <summary>
        /// Executes one FEM forward solve using the current workspace FEM boundary conditions.
        /// Returns the clipped potential distribution.
        /// </summary>
        public PotentialDistribution ForwardSolveStepFem()
        {
            if (_discretization is not FEMMesh mesh)
                throw new TypeInitializationException("Mesh should be of type FEMMesh to use FEM solver!", new Exception("Invalid type in solver!"));

            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            // Ensure workspace has up-to-date electrodes and BCs
            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var boundaryConditions = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(boundaryConditions);

            var potential = _differentialEquationSolver.Solve(mesh, boundaryConditions, null);
            return PotentialClipper.Clip(potential);
        }

        /// <summary>
        /// Executes one LBM forward solve and returns the clipped potential distribution.
        /// </summary>
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

            var potential = _differentialEquationSolver.Solve(lbmGrid, boundaryConditions, null);
            return PotentialClipper.Clip(potential);
        }

        /// <summary>
        /// Executes one LBM forward solve using a CUDA-accelerated path (if the active DE solver supports it).
        /// </summary>
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

            var potential = lbmSolver.CUDASolveForward(lbmGrid, boundaryConditions);
            return PotentialClipper.Clip(potential);
        }

        /// <summary>
        /// Performs a single inverse step on a FEM mesh for a specific boundary condition and measurement vector.
        /// - Runs forward solve to produce φ and simulated boundary data.
        /// - Builds adjoint source from error metric, runs adjoint solve to produce μ.
        /// - Computes element-wise data gradient −(∇μ·∇φ)·Area and regularization gradient.
        /// </summary>
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

        /// <summary>
        /// Performs one inverse step on an LBM grid.
        /// Uses CUDA for forward/adjoint solves if enabled and supported.
        /// </summary>
        public ReconstructionFrame InverseSolveStepLbm(LBMGrid mesh, LBMBoundaryCondition bc, double[] currentMeasurement)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Differential equation solver is null, check calling code!");

            if (_errorMetric == null)
                throw new NullReferenceException("Error metric is null, check calling code!");

            // Update workspace for the current LBM configuration
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
            phi = PotentialClipper.Clip(phi);

            // Extract simulated potentials and project both measured and simulated to the representation
            double[] simulatedPotentials = PotentialClipper.Clip(mesh.GetElectrodePotentials());
            var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
            var electrodeProjectionList = electrodes.Cast<Electrode>().ToList();
            // Normalise Option 1–4 frames (see MeasurementPattern) prior to evaluating the misfit.
            var projection = MeasurementProjector.Create(electrodeProjectionList,
                                                         measurementSetup,
                                                         _usePotentialDifferences,
                                                         currentMeasurement,
                                                         simulatedPotentials);
            Workspace.SetMeasurementPattern(projection.Pattern);

            double currentError = _errorMetric.Evaluate(mesh, projection.Measured, projection.Simulated);
            _ = currentError; // error value is not stored in this frame, but could be logged if needed

            // Error metric based gradient expression
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, projection.Measured, projection.Simulated);
            var expandedAdjoint = projection.ExpandAdjoint(adjSrc);
            Complex[] adjointSource = ToComplex(expandedAdjoint);

            // Adjoint solve using the same boundary condition shape but with source applied
            var adjointBoundaryCondition = new LBMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(adjointBoundaryCondition);
            PotentialDistribution mu = _useCudaParallelization && lbmSolver != null
                ? lbmSolver.CUDASolveAdjoint(mesh, adjointBoundaryCondition, adjointSource)
                : _differentialEquationSolver.Solve(mesh, adjointBoundaryCondition, adjointSource);
            mu = PotentialClipper.Clip(mu);

            // Compute data gradient per element using dot product of gradients
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
                        // Do not update electrode or ghost cells
                        if (el.IsElectrode || el.GhostElement)
                            return 0.0;

                        var gPhi = phiGradientField.GetVector(el.Id);
                        var gMu = muGradientField.GetVector(el.Id);
                        return (gMu.X * gPhi.X + gMu.Y * gPhi.Y);
                    }
                )
            );

            // LBM path in this routine does not compute explicit regularization (left empty)
            return new ReconstructionFrame(dataGrad,
                                           phi,
                                           mu,
                                           new ConductivityDistribution([]),
                                           projection.Measured,
                                           projection.Simulated);
        }

        /// <summary>
        /// Utility that converts a real vector to a Complex vector (imaginary parts set to 0).
        /// </summary>
        private static Complex[] ToComplex(double[] values)
        {
            Complex[] complex = new Complex[values.Length];
            for (int i = 0; i < values.Length; i++)
                complex[i] = values[i];

            return complex;
        }

        /// <summary>
        /// Shared implementation of a FEM inverse step with explicit dependency injection for testing.
        /// Performs: forward solve, misfit projection, adjoint source, adjoint solve, data gradient, and regularizer.
        /// </summary>
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
                // Refresh global caches in the workspace; used by various utilities
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

            // Forward solve: φ
            PotentialDistribution phi = PotentialClipper.Clip(solver.Solve(mesh, bc, null));
            double[] simulatedPotentials = PotentialClipper.Clip(mesh.GetElectrodePotentials());

            var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
            var electrodeProjectionList = electrodes.Cast<Electrode>().ToList();
            // Normalise Option 1–4 frames (see MeasurementPattern) prior to evaluating the misfit.
            var projection = MeasurementProjector.Create(electrodeProjectionList,
                                                         measurementSetup,
                                                         _usePotentialDifferences,
                                                         currentMeasurement,
                                                         simulatedPotentials);
            Workspace.SetMeasurementPattern(projection.Pattern);

            // Build adjoint source from error metric and project back to electrode-space
            var adjSrc = errorMetric.EvaluateAdjointSource(mesh, projection.Measured, projection.Simulated);
            var expandedAdjoint = projection.ExpandAdjoint(adjSrc);
            Complex[] adjointSource = ToComplex(expandedAdjoint);

            foreach(var electrode in electrodes)
            {
                electrode.IsExcitation = false;
                electrode.IsGround = false;
                electrode.IsMeasuring = true;
            }

            // Adjoint solve: μ
            var adjointBoundaryCondition = new FEMBoundaryCondition(new List<FEMElectrode>(electrodes));
            if (updateWorkspace)
                Workspace.SetCurrentGlobalFemBoundaryCondition(adjointBoundaryCondition);

            PotentialDistribution mu = PotentialClipper.Clip(solver.Solve(mesh, adjointBoundaryCondition, adjointSource));

            // Compute ∇φ and ∇μ on elements
            var phiGradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi);
            var muGradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu);

            // Data gradient: −(∇μ·∇φ)·Area per element
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

            // Regularization gradient for the current sigma
            ConductivityDistribution sigma = mesh.GetConductivityDistribution();

            _ = regularizer.EvaluateTerm(mesh, sigma); // evaluate/optionally log; value is not used here
            ConductivityDistribution regularization = regularizer.EvaluateGradient(mesh, sigma);

            return new ReconstructionFrame(dataGrad,
                                           phi,
                                           mu,
                                           regularization,
                                           projection.Measured,
                                           projection.Simulated);
        }

        /// <summary>
        /// CUDA-enabled convenience wrapper for <see cref="InverseSolveStepLbm"/>.
        /// Temporarily forces CUDA path for the duration of the call and restores previous setting.
        /// </summary>
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
        /// Runs the complete FEM reconstruction in a background task.
        /// For each iteration, cycles through the electrode drive pattern:
        /// - sets up excitation/ground electrodes,
        /// - runs inverse steps accumulating the data gradient,
        /// - adds regularization, normalizes, and updates conductivity.
        /// Stops early if <see cref="_stopRequested"/> is set.
        /// </summary>
        private ReconstructionResult RunFemReconstruction(FEMMesh mesh, int maxIterationCount)
        {
            if (_errorMetric == null || _regularizer == null)
                throw new NullReferenceException("Error metric or regularizer not initialised.");

            // --- Prepare reference data --------------------------------------------------

            // If a ground-truth conductivity was provided, simulate frames on that; otherwise on the current mesh
            ConductivityDistribution originalSigma = _originalSigma ?? mesh.DeepCopy().GetConductivityDistribution();
            List<double[]> measurementFrames;
            if (_originalSigma != null)
            {
                FEMMesh measMesh = (FEMMesh)mesh.DeepCopy();
                measMesh.SetConductivityDistribution(originalSigma);
                var parameters = Workspace.GetReconstructionParameters();
                var virtualSettings = parameters?.VirtualElectrodeSettings ?? new VirtualElectrodeSettings();
                var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
                var solver = _differentialEquationSolver ?? throw new NullReferenceException("Differential equation solver not initialised.");
                var simulation = _measurementPersistence.SimulateFemMeasurements(measMesh,
                                                                                1.0,
                                                                                _drivePattern,
                                                                                _usePotentialDifferences,
                                                                                solver,
                                                                                measurementSetup,
                                                                                virtualSettings);
                measurementFrames = simulation.Frames;
            }
            else
            {
                var parameters = Workspace.GetReconstructionParameters();
                var virtualSettings = parameters?.VirtualElectrodeSettings ?? new VirtualElectrodeSettings();
                var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
                var solver = _differentialEquationSolver ?? throw new NullReferenceException("Differential equation solver not initialised.");
                var simulation = _measurementPersistence.SimulateFemMeasurements(mesh,
                                                                                1.0,
                                                                                _drivePattern,
                                                                                _usePotentialDifferences,
                                                                                solver,
                                                                                measurementSetup,
                                                                                virtualSettings);
                measurementFrames = simulation.Frames;
            }

            // Use provided or generated initial sigma
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
                // Accumulate gradient contributions over the full electrode cycle
                var totalGrad = elements.ToDictionary(el => el.Id, _ => 0.0);

                for (int exc = 0; exc < electrodeCount; exc++)
                {
                    // Configure current drive pair and mark non-measuring electrodes
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
                    electrodes[exc % electrodeCount].Current = 10.0;
                    electrodes[(exc + 1) % electrodeCount].IsGround = true;
                    electrodes[(exc + 1) % electrodeCount].IsMeasuring = false;
                    electrodes[(exc + 1) % electrodeCount].Current = -10.0;

                    // Boundary condition reflecting the just configured electrode setup.
                    var bc = new FEMBoundaryCondition(electrodes);
                    Workspace.SetCurrentGlobalFemBoundaryCondition(bc);

                    // Measurement corresponding to this excitation pattern.
                    double[] dObs = measurementFrames[exc];

                    // Perform forward/adjoint solve and obtain the gradient contribution for this electrode pair.
                    var frame = InverseSolveStepFem(mesh, bc, dObs, 1.0); // step size 1.0 -> pure gradient (no scaling)

                    frames.Add(frame);

                    foreach (var kvp in frame.ConductivityGradient.Conductivities)
                        totalGrad[kvp.Key] += kvp.Value;
                }

                // Add regularisation gradient and normalise by the number of electrode pairs.
                var sigma = mesh.GetConductivityDistribution();
                var regGrad = _regularizer.EvaluateGradient(mesh, sigma);
                foreach (var key in totalGrad.Keys.ToList())
                {
                    double g = totalGrad[key] + _regularizationWeight * regGrad.GetConductivity(key);
                    totalGrad[key] = g / electrodeCount;
                }

                // Update the conductivity distribution by taking a step along the accumulated gradient.
                var newSigmaDict = sigma.Conductivities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + _gradientStepSize * totalGrad[kvp.Key]);

                var updatedSigma = ConductivityClipper.Clip(new ConductivityDistribution(newSigmaDict));
                mesh.SetConductivityDistribution(updatedSigma);
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
        /// Background reconstruction routine for Lattice Boltzmann meshes.
        /// Mirrors the FEM routine: for each iteration cycles electrode pairs, accumulates data gradients,
        /// adds optional regularization, normalizes, and updates conductivities.
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
                var simulation = GenerateLbmMeasurements(measMesh, 1.0);
                measurementFrames = simulation.Amplitude.HasValue
                    ? new EITMeasurement(simulation.Frames, simulation.Amplitude.Value, simulation.Pattern)
                    : new EITMeasurement(simulation.Frames, simulation.Pattern);
                Workspace.SetElectrodeMeasurementSetup(simulation.MeasurementSetup);
            }
            else
            {
                var simulation = GenerateLbmMeasurements(mesh, 1.0);
                measurementFrames = simulation.Amplitude.HasValue
                    ? new EITMeasurement(simulation.Frames, simulation.Amplitude.Value, simulation.Pattern)
                    : new EITMeasurement(simulation.Frames, simulation.Pattern);
                Workspace.SetElectrodeMeasurementSetup(simulation.MeasurementSetup);
            }

            ConductivityDistribution initialSigma = _initialSigma ?? mesh.GetConductivityDistribution();
            initialSigma = ConductivityClipper.Clip(initialSigma);
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

                var updated = ConductivityClipper.Clip(new ConductivityDistribution(newSigmaDict));
                mesh.SetConductivityDistribution(updated);
            }

            ConductivityDistribution reconstructed = mesh.GetConductivityDistribution();

            return new ReconstructionResult(mesh,
                                            originalSigma,
                                            initialSigma,
                                            reconstructed,
                                            frames);
        }

        /// <summary>
        /// Legacy/explicit FEM inverse loop over all drive patterns using the provided excitation amplitude.
        /// Preserves the previous API shape and prints diagnostic information to Debug.
        /// </summary>
        public ReconstructionResult InverseSolveFem(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6)
        {
            if(_discretization is not FEMMesh mesh)
                throw new TypeInitializationException("Mesh should be of type FEMMesh to use FEM solver!", new Exception("Invalid type in solver!"));

            if (_regularizer == null)
                throw new NullReferenceException("Regularizer is null, check calling code!");

            if (_numericOptimizer == null)
                throw new NullReferenceException("Numeric optimizer is null, check calling code!");

            _regularizationWeight = redularizationStepSize;

            // Ground truth (if provided), otherwise simulate on current mesh
            ConductivityDistribution originalConductivityDistribution = _originalSigma ?? mesh.DeepCopy().GetConductivityDistribution();
            List<double[]> simulatedMeasurements;
            if (_originalSigma != null)
            {
                FEMMesh measMesh = (FEMMesh)mesh.DeepCopy();
                measMesh.SetConductivityDistribution(originalConductivityDistribution);
                simulatedMeasurements = GenerateFemMeasurements(measMesh, excitationAmplitude);
            }
            else
                simulatedMeasurements = GenerateFemMeasurements(mesh, excitationAmplitude);
            
            // Initial field selection
            ConductivityDistribution initialConductivityDistribution = _initialSigma ?? ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType);
            initialConductivityDistribution = ConductivityClipper.Clip(initialConductivityDistribution);
            mesh.SetConductivityDistribution(initialConductivityDistribution);

            // Prepare workspace caches
            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            Workspace.UpdateCurrentGlobalFemElements(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var bc = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(bc);
            int electrodeCount = electrodes.Count;

            var elements = Workspace.GetCurrentGlobalFemElements();
            int elementCount = elements.Count;

            // Iterative loop with simple stopping criterion
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
                    // Clear electrode status and configure new pair
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

                    // Accumulate
                    foreach (var kvp in dataGrad.Conductivities)
                        totalGrad[kvp.Key] += kvp.Value;
                }

                // Regularization term J_reg and grad ∇J_reg
                double regTerm = _regularizer.EvaluateTerm(mesh, initialConductivityDistribution);
                var regGrad = _regularizer.EvaluateGradient(mesh, initialConductivityDistribution);
                Debug.WriteLine($"Regularization R = {regTerm:0.#####}");

                // Total gradient ∇J = ∇J_data + λ∇R and normalization
                var totalGradDict = totalGrad.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + _regularizationWeight * regGrad.GetConductivity(kvp.Key)
                );

                foreach (var kvp in totalGradDict)
                    totalGradDict[kvp.Key] = kvp.Value / simulatedMeasurements.Count;

                var grad = new ConductivityDistribution(totalGradDict);

                Debug.WriteLine("Gradient ∇J computed.");

                // Apply optimization step
                var newConductivityDistribution = _numericOptimizer.OptimizationStep(mesh.ConductivityDistribution, grad, gradientStepSize);
                newConductivityDistribution = ConductivityClipper.Clip(newConductivityDistribution);

                mesh.SetConductivityDistribution(newConductivityDistribution);

                // Compute total misfit across all patterns for progress monitoring
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

            // Reset elements to initial sigma and produce final result
            foreach (var el in elements)
                el.Conductivity = initialConductivityDistribution.GetConductivity(el.Id);

            ConductivityDistribution reconstructedConductivityDistribution = mesh.GetConductivityDistribution();

            return new ReconstructionResult(mesh, originalConductivityDistribution, initialConductivityDistribution, reconstructedConductivityDistribution, frames);
        }

        /// <summary>
        /// Legacy/explicit LBM inverse loop that defers to the background-style implementation.
        /// Stores gradient and regularization weights and runs the iterative routine.
        /// </summary>
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

        /// <summary>
        /// Convenience wrapper around <see cref="InverseSolveLbm"/> that temporarily forces CUDA for the call.
        /// </summary>
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

        /// <summary>
        /// Computes the total misfit across all drive patterns for a FEM mesh.
        /// Aligns measurement vectors to the current electrode roles, applies representation projection,
        /// and accumulates the error metric value.
        /// </summary>
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
                // Clear electrode status and configure new pair
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
                double[] dSimNew = PotentialClipper.Clip(mesh.GetElectrodePotentials());
                double[] dObs = simulatedMeasurements[exc];
                var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
                var electrodeProjectionList = electrodes.Cast<Electrode>().ToList();
                // Normalise Option 1–4 frames (see MeasurementPattern) prior to evaluating the misfit.
                var projection = MeasurementProjector.Create(electrodeProjectionList,
                                                             measurementSetup,
                                                             _usePotentialDifferences,
                                                             dObs,
                                                             dSimNew);
                Workspace.SetMeasurementPattern(projection.Pattern);
                Jtotal += _errorMetric.Evaluate(mesh, projection.Measured, projection.Simulated);
            }

            return Jtotal;
        }


        #region Lattice Boltzmann Reconstruction

        /// <summary>
        /// Executes a single LBM forward/adjoint pair and returns the data gradient (without regularization).
        /// Uses CUDA for gradient calculations if enabled.
        /// </summary>
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
            PotentialDistribution phi = PotentialClipper.Clip(_differentialEquationSolver.Solve(mesh, bc, null));

            // Extract simulated potentials
            double[] simulatedPotentials = PotentialClipper.Clip(mesh.GetElectrodePotentials());
            var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
            var electrodeProjectionList = electrodes.Cast<Electrode>().ToList();
            var projection = MeasurementProjector.Create(electrodeProjectionList,
                                                         measurementSetup,
                                                         _usePotentialDifferences,
                                                         currentMeasurement,
                                                         simulatedPotentials);
            Workspace.SetMeasurementPattern(projection.Pattern);

            double currentError = _errorMetric.Evaluate(mesh, projection.Measured, projection.Simulated);
            _ = currentError;

            // Error metric based gradient expression
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, projection.Measured, projection.Simulated);
            var expandedAdjoint = projection.ExpandAdjoint(adjSrc);
            Complex[] adjointSource = ToComplex(expandedAdjoint);

            var adjointBoundaryCondition = new LBMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalLbmBoundaryCondition(adjointBoundaryCondition);
            PotentialDistribution mu = PotentialClipper.Clip(_differentialEquationSolver.Solve(mesh, adjointBoundaryCondition, adjointSource));

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

            return new ReconstructionFrame(dataGrad,
                                          phi,
                                          mu,
                                          new ConductivityDistribution([]),
                                          projection.Measured,
                                          projection.Simulated);
        }

        /// <summary>
        /// High-level multi-iteration LBM inverse routine that repeatedly calls <see cref="LbmSolveStep"/>
        /// and updates the conductivity field by gradient descent.
        /// </summary>
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
                var simulation = GenerateLbmMeasurements(measMesh, 1.0);
                measurementFrames = simulation.Amplitude.HasValue
                    ? new EITMeasurement(simulation.Frames, simulation.Amplitude.Value, simulation.Pattern)
                    : new EITMeasurement(simulation.Frames, simulation.Pattern);
                Workspace.SetElectrodeMeasurementSetup(simulation.MeasurementSetup);
            }
            else
            {
                var simulation = GenerateLbmMeasurements(lbmGrid, 1.0);
                measurementFrames = simulation.Amplitude.HasValue
                    ? new EITMeasurement(simulation.Frames, simulation.Amplitude.Value, simulation.Pattern)
                    : new EITMeasurement(simulation.Frames, simulation.Pattern);
                Workspace.SetElectrodeMeasurementSetup(simulation.MeasurementSetup);
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
                        kvp => kvp.Value - step * frame.ConductivityGradient.GetConductivity(kvp.Key)                        
                    );

                    var updatedSigma = ConductivityClipper.Clip(new ConductivityDistribution(newSigmaDict));
                    lbmGrid.SetConductivityDistribution(updatedSigma);

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

        #endregion

        #region Finite Element Method Reconstrucion

        /// <summary>
        /// Runs a FEM forward solve and writes the resulting potential distribution back to the mesh.
        /// The mesh is returned for fluent-style chaining.
        /// </summary>
        public FEMMesh SolveFemForward(FEMMesh mesh)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Cannot perform Finite Element forward solve, differential equation solver is not specified!");

            var conductivitiyDistribution = mesh.GetConductivityDistribution();
            _ = conductivitiyDistribution; // value not used here, but access ensures state is current

            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var boundaryConditions = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(boundaryConditions);

            PotentialDistribution potentialDistribution = PotentialClipper.Clip(_differentialEquationSolver.Solve(mesh, boundaryConditions, null));
            mesh.SetPotentialDistribution(potentialDistribution);

            return mesh;
        }

        /// <summary>
        /// End-to-end FEM inverse over all measurement frames using the configured drive pattern.
        /// Performs gradient accumulation across frames, adds regularization, and updates sigma via the optimizer.
        /// Produces debug output and supports early stopping on misfit improvement stagnation.
        /// </summary>
        public FEMMesh SolveFemInverseAllFrames(FEMMesh mesh, int maxIterCount, double stepSize, double regularization, double excitationAmplitude = 1.0, double tolerance = 1e-5, double minConductivtiy = 1e-3, double maxConductivity = 10.0)
        {
            throw new NotImplementedException();
            if (_differentialEquationSolver == null ||
                _errorMetric == null ||
                _regularizer == null ||
                _numericOptimizer == null)
                throw new NullReferenceException("Some solver parameter is null, the solver must properly be initialized, throguh the layer, check code!");

            _regularizationWeight = regularization;

            List<double[]> simulatedMeasurements = GenerateFemMeasurements(mesh, excitationAmplitude);
            
            // 2) Initialize conductivity (σ^{(0)}) based on user selection
            ConductivityDistribution sigma = ConductivityClipper.Clip(ConductivityDistributionFactory.CreateInitialDistribution(mesh, _initialDistributionType));
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

                    // 4a) Forward solve φ⁽ᵏ⁾ = S(σ⁽ᵏ⁾)
                    PotentialDistribution phi = PotentialClipper.Clip(_differentialEquationSolver.Solve(mesh, bc, null));
                    Debug.WriteLine("Forward φ computed.");

                    // 4b) Extract simulated boundary data d_sim
                    double[] dSim = PotentialClipper.Clip(mesh.GetElectrodePotentials());
                    Debug.WriteLine("The simulated electrode potentials during iteration:");
                    for (int i = 0; i < dSim.Length; i++)
                        Debug.WriteLine($"{dSim[i]}");

                    double[] dObs = simulatedMeasurements[exc];

                    // 4e) Build adjoint source s = EvaluateAdjointSource (L2: residual; W2: Kantorovich φ) 
                    var adjSrc = PotentialClipper.Clip(_errorMetric.EvaluateAdjointSource(mesh, dObs, dSim));
                    //var expandedAdjoint = BuildAdjointSourceVector(adjSrc, electrodes.Count);
                    //Complex[] adjointSource = ToComplex(expandedAdjoint);

                    // wrap into a PotentialDistribution on electrodes
                    //var srcDist = new PotentialDistribution(
                    //    Enumerable.Range(0, expandedAdjoint.Length)
                    //              .ToDictionary(i => electrodes[i].Id, i => expandedAdjoint[i])
                    //);

                    // 4f) Adjoint solve μ: same forward‐solver but feed in adjSrc as boundary currents
                    //var adjointBoundaryCondition = new FEMBoundaryCondition(electrodes, srcDist);
                    //Workspace.SetCurrentGlobalFemBoundaryCondition(adjointBoundaryCondition);
                    //var mu = PotentialClipper.Clip(_differentialEquationSolver.Solve(mesh, adjointBoundaryCondition, adjointSource));
                    Debug.WriteLine("Adjoint μ computed.");

                    // 4g) Compute gradient ∇J_data = ∇μ·∇φ elementwise
                    //var phiGradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi);
                    //var muGradient = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu);

                    //var dataGrad = new ConductivityDistribution(
                    //    Workspace.GetCurrentGlobalFemElements().ToDictionary(
                    //        el => el.Id,
                    //        el => {
                    //            // compute ∇φ, ∇μ on this element
                    //            var gPhi = phiGradient.GetVector(el.Id);
                    //            var gMu = muGradient.GetVector(el.Id);
                    //            return (gMu.X * gPhi.X + gMu.Y * gPhi.Y) * el.Area;
                    //        }
                    //    )
                    //);
                    //
                    //foreach (var kvp in dataGrad.Conductivities)
                    //    totalGrad[kvp.Key] += kvp.Value;
                }

                // 4d) Regularization term J_reg and grad ∇J_reg
                double regTerm = _regularizer.EvaluateTerm(mesh, sigma);
                var regGrad = _regularizer.EvaluateGradient(mesh, sigma);
                Debug.WriteLine($"Regularization R = {regTerm:0.#####}");

                // 4h) Total gradient ∇J = ∇J_data + ∇R and normalization
                var totalGradDict = totalGrad.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value + _regularizationWeight * regGrad.GetConductivity(kvp.Key)
                );

                foreach (var kvp in totalGradDict)
                    totalGradDict[kvp.Key] = kvp.Value / simulatedMeasurements.Count;

                var grad = new ConductivityDistribution(totalGradDict);

                Debug.WriteLine("Gradient ∇J computed.");

                // 4i) Apply optimization step
                var newConductivityDistribution = _numericOptimizer.OptimizationStep(mesh.ConductivityDistribution, grad, stepSize);
                newConductivityDistribution = ConductivityClipper.Clip(newConductivityDistribution);

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

                    var phiNew = PotentialClipper.Clip(_differentialEquationSolver.Solve(mesh, bc, null));
                    double[] dSimNew = PotentialClipper.Clip(mesh.GetElectrodePotentials());
                    double[] dObs = simulatedMeasurements[exc];
                    var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
                    var electrodeProjectionList = electrodes.Cast<Electrode>().ToList();
                    // Normalise Option 1–4 frames (see MeasurementPattern) prior to evaluating the misfit.
                    var projection = MeasurementProjector.Create(electrodeProjectionList,
                                                                 measurementSetup,
                                                                 _usePotentialDifferences,
                                                                 dObs,
                                                                 dSimNew);
                    Workspace.SetMeasurementPattern(projection.Pattern);
                    Jtotal += _errorMetric.Evaluate(mesh, projection.Measured, projection.Simulated);
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

            // Reset elements to initial sigma and produce final result
            foreach (var el in elements)
                el.Conductivity = sigma.GetConductivity(el.Id);

            return mesh;
        }

        private List<double[]> GenerateFemMeasurements(FEMMesh mesh, double excitationAmplitude)
        {
            var parameters = Workspace.GetReconstructionParameters();
            var virtualSettings = parameters?.VirtualElectrodeSettings ?? new VirtualElectrodeSettings();
            var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
            var solver = _differentialEquationSolver ?? throw new NullReferenceException("Differential equation solver not initialised.");

            return _measurementPersistence
                .SimulateFemMeasurements(mesh,
                                          excitationAmplitude,
                                          _drivePattern,
                                          _usePotentialDifferences,
                                          solver,
                                          measurementSetup,
                                          virtualSettings)
                .Frames;
        }

        private MeasurementSimulationResult GenerateLbmMeasurements(LBMGrid grid, double excitationAmplitude)
        {
            var parameters = Workspace.GetReconstructionParameters();
            var virtualSettings = parameters?.VirtualElectrodeSettings ?? new VirtualElectrodeSettings();
            var measurementSetup = Workspace.GetElectrodeMeasurementSetup();
            var solver = _differentialEquationSolver ?? throw new NullReferenceException("Differential equation solver not initialised.");

            return _measurementPersistence.SimulateLbmMeasurements(grid,
                                                                    excitationAmplitude,
                                                                    _drivePattern,
                                                                    _usePotentialDifferences,
                                                                    solver,
                                                                    measurementSetup,
                                                                    virtualSettings);
        }

        #endregion

        /// <summary>
        /// Performs a forward solve on the dual graph representation of the mesh and writes potentials back.
        /// </summary>
        public FEMMesh SolveGraphForward(FEMMesh mesh)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Cannot perform graph based forward solve, differential equation solver is not specified!");

            Workspace.UpdateCurrentGlobalFemElectrodes(mesh);
            var electrodes = Workspace.GetCurrentGlobalFemElectrodes();
            var bc = new FEMBoundaryCondition(electrodes);
            Workspace.SetCurrentGlobalFemBoundaryCondition(bc);
            var pd = PotentialClipper.Clip(_differentialEquationSolver.Solve(mesh, bc, null));
            mesh.SetPotentialDistribution(pd);
            return mesh;
        }

        /// <summary>
        /// Executes one gradient-descent step for the graph-based inverse problem.
        /// The adjoint field μ is obtained from the residual (d_meas − d_sim) and used
        /// to update edge conductances via (∇φ·∇μ) on the graph. Path is currently incomplete.
        /// </summary>
        public ReconstructionResult InverseSolveStepGraph(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize)
        {
            if (_differentialEquationSolver is not GraphSolver graphSolver || _numericSolver == null || _errorMetric == null)
                throw new NullReferenceException("Graph solver path not initialized.");

            PotentialDistribution phi = PotentialClipper.Clip(_differentialEquationSolver.Solve(mesh, boundaryCondition, null));
            var dSim = PotentialClipper.Clip(mesh.GetElectrodePotentials());
            var adjSrc = PotentialClipper.Clip(_errorMetric.EvaluateAdjointSource(mesh, measurement, dSim));
            var adjComplex = adjSrc.Select(x => new Complex(x, 0.0)).ToArray();
            PotentialDistribution mu = PotentialClipper.Clip(graphSolver.SolveAdjoint(mesh, _numericSolver, adjComplex));
            ConductivityDistribution original = mesh.GetConductivityDistribution();
            ConductivityDistribution sigma = ConductivityClipper.Clip(graphSolver.InverseSolve(mesh, boundaryCondition, adjComplex));
            mesh.SetConductivityDistribution(sigma);

            // TODO: return a proper ReconstructionResult for graph path when data structures are stabilized
            throw new NotImplementedException();
            //return new ReconstructionResult(mesh, phi, mu, original, original, sigma);
        }

        // --- Persistence ---
        /// <summary>
        /// Persists the reconstruction frames and metadata via the repository implementation.
        /// </summary>
        public void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters)
            => _reconstructionRepository.SaveReconstruction(frames, name, parameters);

        /// <summary>
        /// Enumerates saved reconstructions (metadata only).
        /// </summary>
        public IEnumerable<ReconstructionInfo> GetReconstructions() => _reconstructionRepository.GetReconstructions();

        /// <summary>
        /// Loads a previously saved reconstruction from a file path.
        /// </summary>
        public List<ReconstructionResult> LoadReconstruction(string filePath) => _reconstructionRepository.LoadReconstruction(filePath);

        public IDifferentialEquationSolver? GetDifferentialEquationSolver() => _differentialEquationSolver;
    }
}
