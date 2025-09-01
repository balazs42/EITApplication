using DataAccessLayer;
using System.Diagnostics;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
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

        public ReconstructionPersistence(IDAQRepository daqRepository, IReconstructionRepository reconstructionRepository)
        {
            _daqRepository = daqRepository;
            _reconstructionRepository = reconstructionRepository;
        }


        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters)
        {
            _mesh = mesh;

            _numericSolver = NumericSolverFactory.Create(parameters.NumericSolver);
            _differentialEquationSolver = DifferentialEquationSolverFactory.Create(mesh, parameters.DifferentialEquationSolver, _numericSolver);
            _regularizer = RegularisationFactory.Create(parameters.RegularizationTechnique, _mesh);
            _errorMetric = ErrorMetricFactory.Create(parameters.ErrorMetric);
            _numericOptimizer = NumericOptimizerFactory.Create(parameters.NumericOptimizer, ConductivityDistributionFactory.CreateSlightlyDiffering(mesh));

            _inverseModel = InverseModelFactory.Create(_mesh, _numericOptimizer, _regularizer, _errorMetric, _differentialEquationSolver);
        }

        #region Lattice Boltzmann Reconstruction

        public PotentialDistribution SolveLbmForward()
        {
            LBMMesh? mesh = _mesh as LBMMesh;

            if (_inverseModel == null || _mesh == null || mesh == null || _differentialEquationSolver == null)
                throw new NullReferenceException();

            var electrodes = mesh.GetElectrodes().Cast<LBMElectrode>().ToList();

            LBMBoundaryCondition bc = new(electrodes);

            return _differentialEquationSolver.Solve(_mesh, bc, null);
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

            // --- Inverse Solver Iterations ---

            // Loop to run the inverse iterations
            for(int i = 0; i < maxIterationCount; i++)
            {
                // One iteration run on the whole measurement frame
                for(int j = 0; j < measurementFrames.Frames.Count; j++)
                {
                    // Solve Forward to extract simulated potentials
                    var phi = _differentialEquationSolver.Solve(mesh, bc, null);

                    // Extract simulated potentials
                    double[] simulatedPotentials = mesh.GetElectrodePotentials();

                    double[] currentMeasurement = measurementFrames.GetNextFrame();

                    double currentError = _errorMetric.Evaluate(mesh, currentMeasurement, simulatedPotentials);

                    // Error metric based gradeint expression
                    var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, currentMeasurement, simulatedPotentials);

                    Complex[] adjointSource = new Complex[adjSrc.Length];
                    for (int k = 0; k < adjSrc.Length; k++)
                        adjointSource[k] = adjSrc[k];

                    var mu = _differentialEquationSolver.Solve(mesh, new LBMBoundaryCondition(electrodes), adjointSource);

                    var dataGrad = new ConductivityDistribution(
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

                    // --- Set new conductivities ---
                    double step = stepSize;  // choose small enough for stability
                    var sigma = mesh.GetConductivityDistribution();

                    var newSigmaDict = sigma.Conductivities.ToDictionary(
                        kvp => kvp.Key,
                        // Clamp conductivites which got below 0
                        kvp => ((kvp.Value - step * dataGrad.GetConductivity(kvp.Key)) < 0.0) ? 1e-1 : kvp.Value - step * dataGrad.GetConductivity(kvp.Key)
                    );

                    sigma = new ConductivityDistribution(newSigmaDict);
                    mesh.SetConductivityDistribution(sigma);
                }
            }

            ConductivityDistribution reconstructedConductivityDistribution = mesh.GetConductivityDistribution();

            return new ReconstructionResult((LBMMesh)_mesh,
                                            _differentialEquationSolver.Solve(_mesh, bc, null),
                                            new PotentialDistribution(new()),
                                            originalConductivityDistribution,
                                            originalConductivityDistribution,
                                            reconstructedConductivityDistribution);
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
                var solution = _differentialEquationSolver.Solve(mesh, boundaryCondition, null);
                
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
            
            // 2) Initialize conductivity (σ^{(0)}) to homogeneous distribution
            ConductivityDistribution sigma = ConductivityDistributionFactory.CreateSlightlyDiffering(mesh, 0.95);
            mesh.SetConductivityDistribution(sigma);

            List<FEMElectrode> electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();
            var bc = new FEMBoundaryCondition(electrodes);
            int electrodeCount = electrodes.Count;

            var elements = mesh.GetElements().Cast<FEMElement>().ToList();
            int elementCount = elements.Count;

            // 4) Iterative loop
            double prevJ = double.PositiveInfinity;
            List<double> errors = [];

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

            var phi = _differentialEquationSolver.Solve(mesh, boundaryCondition, null);
            var dSim = mesh.GetElectrodePotentials();
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, measurement, dSim);
            var adjComplex = adjSrc.Select(x => new Complex(x, 0.0)).ToArray();
            var mu = graphSolver.SolveAdjoint(mesh, _numericSolver, adjComplex);
            var original = mesh.GetConductivityDistribution();
            var sigma = graphSolver.InverseSolve(mesh, boundaryCondition, adjComplex);
            mesh.SetConductivityDistribution(sigma);
            return new ReconstructionResult(mesh, phi, mu, original, original, sigma);
        }

        public ReconstructionResult InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize)
        {
            FEMMesh deepCopy = (FEMMesh)mesh.DeepCopy();
            ConductivityDistribution originalConductivityDistribution = deepCopy.ConductivityDistribution;

            // Initialize inverse solver
            _differentialEquationSolver = DifferentialEquationSolverFactory.Create(mesh, DifferentialEquationSolver.FiniteElementMethod, NumericSolverFactory.Create(NumericSolver.SVD));
            _errorMetric = ErrorMetricFactory.Create(ErrorMetric.L2);
            _regularizer = RegularisationFactory.Create(RegularizationTechnique.ZeroOrderTikhonov, mesh.DeepCopy(), 0.0);
            _numericOptimizer = NumericOptimizerFactory.Create(NumericOptimizer.GradientBased, ConductivityDistributionFactory.CreateRandom(mesh));

            // Initialize conductivity (σ^{(0)}) to homogeneous distribution
            ConductivityDistribution sigma0 = ConductivityDistributionFactory.CreateSlightlyDiffering(mesh, 0.95);
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
            var mu = _differentialEquationSolver.Solve(mesh, new FEMBoundaryCondition(electrodes, srcDist), adjointSource);
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
            var regGrad = _regularizer.EvaluateGradient(mesh, sigma0);
            Debug.WriteLine($"Regularization R = {regTerm:0.#####}");

            // 4h) Total gradient ∇J = ∇J_data + ∇R  (Eq. 2.1.31)
            var totalGradDict = totalGrad.ToDictionary(kvp => kvp.Key, kvp => kvp.Value + regGrad.GetConductivity(kvp.Key));

            var grad = new ConductivityDistribution(totalGradDict);

            Debug.WriteLine("Gradient ∇J computed.");

            // 4i) Apply optimization step
            mesh.SetConductivityDistribution(_numericOptimizer.OptimizationStep(mesh.ConductivityDistribution, grad, stepSize));

            return new ReconstructionResult(mesh, mesh.PotentialDistribution, mu, originalConductivityDistribution, sigma0, mesh.ConductivityDistribution);
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

