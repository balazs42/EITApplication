using DataAccessLayer;
using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.Models;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers.FiniteElementSolver;

namespace BusinessLayer
{
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        private readonly IDAQRepository _daqRepository;

        private InverseModel? _inverseModel = null;
        
        private IMesh? _mesh = null;
        private INumericSolver? _numericSolver = null;
        private IDifferentialEquationSolver? _differentialEquationSolver = null;
        private IRegularizer? _regularizer = null;
        private IErrorMetric? _errorMetric = null;
        private INumericOptimizer? _numericOptimizer = null;

        public ReconstructionPersistence(IDAQRepository daqRepository)
        {
            _daqRepository = daqRepository;
        }

        public async Task<ReconstructionResult> GetReconstructionResult()
        {
        LBMMesh? mesh = _mesh as LBMMesh;
            
            if (_inverseModel == null || _mesh == null || mesh == null || _differentialEquationSolver == null)
                throw new NullReferenceException();

            // Generate initial distribution for the reconstruction process
            //ConductivityDistribution initialDistribution = PriorConductivityDistributionGenerator.GenerateHomogeneousDistribution(_mesh);

            // TODO: Get the current measurement
            // EITMeasurement measurement = _daqRepository.GetEITMeasurement();

            //_inverseModel.Solve(initialDistribution, measurement, 100);

            /* supply real data, mesh, and initial σ */
            //var result = await Task.Run(() => 
            //    _inverseModel.Solve(initialDistribution, measurement, 50)            
            //);
            var electrodes = mesh.Electrodes;

            LBMBoundaryCondition bc = new(electrodes);

            return new ReconstructionResult((LBMMesh)_mesh, _differentialEquationSolver.SolveForward(_mesh, bc), new PotentialDistribution(new()), ConductivityDistributionFactory.CreateRandom(_mesh), ConductivityDistributionFactory.CreateRandom(_mesh), ConductivityDistributionFactory.CreateRandom(_mesh));

            throw new NotImplementedException();
            //ConductivityDistribution result = new ConductivityDistribution(new());
            //ReconstructionResult reconstructionResult = new ReconstructionResult((_mesh is FEMMesh) ? (FEMMesh)_mesh : (LBMMesh)_mesh, result);
            //return reconstructionResult;
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
       
        private EITMeasurement LBMSimulateDummyMeasurement()
        {
            const int size = 16;

            double[,] meas = new double[size, size];
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    meas[i, j] = 1.0;

            return new EITMeasurement(meas);
        }

        #region Finite Element Method Related Functions

        public FEMMesh SolveFemForward(FEMMesh mesh)
        {
            if (_differentialEquationSolver == null)
                throw new NullReferenceException("Cannot perform Finite Element forward solve, differential equation solver is not specified!");

            var conductivitiyDistribution = mesh.GetConductivityDistribution();

            var electrodes = mesh.Electrodes.Cast<FEMElectrode>().ToList();

            BoundaryCondition boundaryConditions = new FEMBoundaryCondition(electrodes);

            PotentialDistribution potentialDistribution = _differentialEquationSolver.SolveForward(mesh, boundaryConditions);
            mesh.SetPotentialDistribution(potentialDistribution);

            return mesh;
        }

        public FEMMesh SolveFemInverse(FEMMesh mesh, int maxIterCount, double stepSize, double regularization)
        {
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
            var electrodes = mesh.Electrodes.Cast<FEMElectrode>().ToList();
            var bc = new FEMBoundaryCondition(electrodes);

            // 4) Iterative loop
            double prevError = double.PositiveInfinity;
            List<double> errors = [];

            for (int iter = 0; iter < maxIterCount; iter++)
            {
                Debug.WriteLine($"\n=== Inverse iteration {iter} ===");

                // 4a) Forward solve φ⁽ᵏ⁾ = S(σ⁽ᵏ⁾)   (thesis Eq. 1.1.16)
                PotentialDistribution phi = _differentialEquationSolver.SolveForward(mesh, bc);
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
                    .SolveForward(mesh,
                                  new FEMBoundaryCondition(electrodes, srcDist));
                Debug.WriteLine("Adjoint μ computed.");

                // 4g) Compute gradient ∇J_data = ∇μ·∇φ elementwise  (thesis Eq. 2.1.20)
                var dataGrad = new ConductivityDistribution(
                    mesh.Elements.ToDictionary(
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


            // 5) Update mesh ConductivityDistribution and return
            foreach (var el in mesh.Elements)
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

            List<double[]> simulatedMeasurements = SimulateFemMeasurements(mesh, excitationAmplitude);
            
            // 2) Initialize conductivity (σ^{(0)}) to homogeneous distribution
            ConductivityDistribution sigma = ConductivityDistributionFactory.CreateSlightlyDiffering(mesh, 0.95);
            mesh.SetConductivityDistribution(sigma);

            List<FEMElectrode> electrodes = mesh.Electrodes.Cast<FEMElectrode>().ToList();
            var bc = new FEMBoundaryCondition(electrodes);
            var electrodeCount = mesh.Electrodes.Count;

            // 4) Iterative loop
            double prevJ = double.PositiveInfinity;
            List<double> errors = [];

            for (int iter = 0; iter < maxIterCount; iter++)
            {
                Debug.WriteLine($"\n=== Inverse iteration {iter} ===");

                Dictionary<int, double> totalGrad = new();
                for (int i = 0; i < mesh.Elements.Count; i++)
                    totalGrad.Add(i, 0.0);

                for(int exc = 0; exc < mesh.Electrodes.Count; exc++)
                {
                    // Clear electrode status
                    foreach (var el in mesh.Electrodes)
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

                    bc = new FEMBoundaryCondition(mesh.Electrodes);

                    // 4a) Forward solve φ⁽ᵏ⁾ = S(σ⁽ᵏ⁾)   (thesis Eq. 1.1.16)
                    PotentialDistribution phi = _differentialEquationSolver.SolveForward(mesh, bc);
                    Debug.WriteLine("Forward φ computed.");

                    // 4b) Extract simulated boundary data d_sim
                    double[] dSim = mesh.GetElectrodePotentials();
                    Debug.WriteLine("The simulated electrode potentials during iteration:");
                    for (int i = 0; i < dSim.Length; i++)
                        Debug.WriteLine($"{dSim[i]}");

                    double[] dObs = simulatedMeasurements[exc];

                    // 4e) Build adjoint source s = EvaluateAdjointSource (L2: residual; W2: Kantorovich φ) 
                    var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, dObs, dSim);
                    // wrap into a PotentialDistribution on electrodes
                    var srcDist = new PotentialDistribution(
                        Enumerable.Range(0, adjSrc.Length)
                                  .ToDictionary(i => electrodes[i].Id, i => adjSrc[i])
                    );

                    // 4f) Adjoint solve μ: same forward‐solver but feed in adjSrc as boundary currents
                    var mu = _differentialEquationSolver.SolveForward(mesh, new FEMBoundaryCondition(electrodes, srcDist));
                    Debug.WriteLine("Adjoint μ computed.");

                    // 4g) Compute gradient ∇J_data = ∇μ·∇φ elementwise  (thesis Eq. 2.1.20)
                    var dataGrad = new ConductivityDistribution(
                        mesh.Elements.ToDictionary(
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
                    kvp => kvp.Value + regGrad.GetConductivity(kvp.Key)
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
                    foreach (var el in mesh.Electrodes)
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

                    var phiNew = _differentialEquationSolver.SolveForward(mesh, bc);
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
            foreach (var el in mesh.Elements)
                el.Conductivity = sigma.GetConductivity(el.Id);

            return mesh;
        }

        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude)
        {
            List<double[]> measurements = [];

            FEMMesh deepCopy = mesh.DeepCopy();
            var electrodes = deepCopy.Electrodes;
            int electrodeCount = deepCopy.Electrodes.Count;
            for (int i = 0; i < electrodeCount; i++)
            {
                // Clear electrode status
                foreach(var el in deepCopy.Electrodes)
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

        public ReconstructionResult InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize)
        {
            FEMMesh deepCopy = mesh.DeepCopy();
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
            List<FEMElectrode> electrodes = mesh.Electrodes.Cast<FEMElectrode>().ToList();
            var bc = new FEMBoundaryCondition(electrodes);
            var electrodeCount = mesh.Electrodes.Count;

            // Forward solve  (thesis Eq. 1.1.16)
            PotentialDistribution phi = _differentialEquationSolver.SolveForward(mesh, bc);
            Debug.WriteLine("Forward φ computed.");

            // Extract simulated boundary data d_sim
            double[] dSim = mesh.GetElectrodePotentials();
            double[] dObs = measurement;

            // Build adjoint source s = EvaluateAdjointSource (L2: residual; W2: Kantorovich φ) 
            var adjSrc = _errorMetric.EvaluateAdjointSource(mesh, dObs, dSim);
           
            // wrap into a PotentialDistribution on electrodes
            var srcDist = new PotentialDistribution(Enumerable.Range(0, adjSrc.Length).ToDictionary(i => electrodes[i].Id, i => adjSrc[i]));

            // 4f) Adjoint solve μ: same forward‐solver but feed in adjSrc as boundary currents
            var mu = _differentialEquationSolver.SolveForward(mesh, new FEMBoundaryCondition(electrodes, srcDist));
            Debug.WriteLine("Adjoint μ computed.");

            // 4g) Compute gradient ∇J_data = ∇μ·∇φ elementwise  (thesis Eq. 2.1.20)
            var dataGrad = new ConductivityDistribution(
                mesh.Elements.ToDictionary(
                    el => el.Id,
                    el => {
                        // compute ∇φ, ∇μ on this element
                        var gPhi = FiniteElementOperators.CalculateElementWiseGradient(mesh, phi).GetVector(el.Id);
                        var gMu = FiniteElementOperators.CalculateElementWiseGradient(mesh, mu).GetVector(el.Id);
                        return (gMu.X * gPhi.X + gMu.Y * gPhi.Y) * el.Area;
                    }
                )
            );

            Dictionary<int, double> totalGrad = new();
            for (int i = 0; i < mesh.Elements.Count; i++)
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
            mesh.ConductivityDistribution = _numericOptimizer.OptimizationStep(mesh.ConductivityDistribution, grad, stepSize);

            return new ReconstructionResult(mesh, mesh.PotentialDistribution, mu, originalConductivityDistribution, sigma0, mesh.ConductivityDistribution);
        }
    }
}

