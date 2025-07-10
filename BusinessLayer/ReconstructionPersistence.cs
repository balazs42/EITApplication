using DataAccessLayer;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing;
using Utility.Classes.Models;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers;

namespace BusinessLayer
{
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        private readonly IDAQRepository _daqRepository;

        private InverseModel _inverseModel;

        private EITMeasurements _measurementData;

        private IMesh _mesh;
        private INumericSolver _numericSolver;
        private IDifferentialEquationSolver _differentialEquationSolver;
        private IRegularizer _regularizer;
        private IErrorMetric _errorMetric;
        private INumericOptimizer _numericOptimizer;

        public ReconstructionPersistence(IDAQRepository daqRepository)
        {
            _daqRepository = daqRepository;
        }

        public async Task<ReconstructionResult> GetReconstructionResult()
        {
            if (_inverseModel == null)
                throw new InvalidOperationException();

            // Generate initial distribution for the reconstruction process
            ConductivityDistribution initialDistribution = PriorConductivityDistributionGenerator.GenerateHomogeneousDistribution(_mesh);

            // TODO: Get the current measurement
            // EITMeasurement measurement = _daqRepository.GetEITMeasurement();

            EITMeasurements measurement = SimulateMeasurement();

            _inverseModel.Solve(initialDistribution, measurement, 100);

            /* supply real data, mesh, and initial σ */
            //var result = await Task.Run(() => 
            //    _inverseModel.Solve(initialDistribution, measurement, 50)            
            //);
            throw new NotImplementedException();
            ConductivityDistribution result = new ConductivityDistribution(new());
            ReconstructionResult reconstructionResult = new ReconstructionResult((_mesh is FEMMesh) ? (FEMMesh)_mesh : (LBMMesh)_mesh, result);
            return reconstructionResult;
        }

        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters)
        {
            _mesh = mesh;

            _numericSolver = NumericSolverFactory.Create(parameters.NumericSolver);
            _differentialEquationSolver = DifferentialEquationSolverFactory.Create(mesh, parameters.DifferentialEquationSolver, _numericSolver);
            _regularizer = RegularisationFactory.Create(parameters.RegularizationTechnique, _mesh);
            _errorMetric = ErrorMetricFactory.Create(parameters.ErrorMetric);
            _numericOptimizer = NumericOptimizerFactory.Create(parameters.NumericOptimizer);

            _inverseModel = InverseModelFactory.Create(_mesh, _numericOptimizer, _regularizer, _errorMetric, _differentialEquationSolver);
        }
        /// <summary>
        /// Creates a realistic, synthetic EIT measurement by simulating the physics
        /// on a known "ground truth" conductivity map.
        /// </summary>
        private EITMeasurements SimulateMeasurement()
        {
            Debug.WriteLine("Simulating measurement data from phantom...");

            // --- Step 1: Create the Ground Truth Phantom ---
            // We use an LBMMesh for the simulation.
            var groundTruthMesh = new LBMMesh(15, 15);

            // Create a custom conductivity map with a high-conductivity rectangle inside.
            var conductivityDict = new Dictionary<int, double>();
            int centerX = groundTruthMesh.Nx / 2;
            int centerY = groundTruthMesh.Ny / 2;
            int featureSize = 3;
            foreach (var element in groundTruthMesh.Elements.Cast<LBMElement>())
            {
                var (x, y) = groundTruthMesh.ToLattice(element.Id);
                bool isFeature = Math.Abs(x - centerX) < featureSize && Math.Abs(y - centerY) < featureSize;
                conductivityDict[element.Id] = isFeature ? 0.001 : 1.0;
            }
            var groundTruthConductivity = new ConductivityDistribution(conductivityDict);

            groundTruthMesh.ConductivityDistribution = groundTruthConductivity;

            // --- Step 2: Simulate the 16 Drive Patterns ---
            const int numElectrodes = 16;
            var measurementMatrix = new double[numElectrodes, numElectrodes];

            // Get the list of physical electrodes that were created with the mesh.
            var physicalElectrodes = groundTruthMesh.GetElectrodes();

            for (int i = 0; i < numElectrodes; i++)
            {
                // For each drive pattern, we need to configure the specific currents.
                int sourceId = i;
                int sinkId = (i + 1) % numElectrodes;

                var dummyMeas = SimulateDummyMeasurement().GetMeasurement(i);

                var bc = new BoundaryCondition(physicalElectrodes);

                // Run the LBM forward solver using the ground truth conductivity and this drive pattern.
                var potentialDistribution = _differentialEquationSolver.SolveForward(groundTruthMesh, bc);

                // After the solve, the GetElectrodePotentials method will return the calculated
                // potentials, including NaNs for the driving electrodes.
                double[] resultingPotentials = groundTruthMesh.GetElectrodePotentials();

                // Copy this result into the correct row of our final 16x16 matrix.
                for (int j = 0; j < numElectrodes; j++)
                {
                    measurementMatrix[i, j] = resultingPotentials[j];
                }

                Debug.WriteLine("Calculated Potential Field:");
                potentialDistribution.LogDistribution();
                Debug.WriteLine("---------------------------");
            }

            Debug.WriteLine("Finished simulating measurement data.");

            // --- Step 3: Wrap the final matrix in the EITMeasurement object ---
            return new EITMeasurements(measurementMatrix);
        }

        private EITMeasurements SimulateDummyMeasurement()
        {
            const int size = 16;

            double[,] meas = new double[size, size];
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    meas[i, j] = 1.0;

            // Loop through each row to place the three NaN values.
            for (int i = 0; i < size; i++)
            {
                // For each row 'i', the NaN values are at columns i, i-1, and i+1.
                // We use modulo arithmetic to handle the "wrap-around" at the edges.

                // The main diagonal index is simply 'i'.
                int diagonalIndex = i;

                // The index for the diagonal "above" the main one.
                // (15 + 1) % 16 = 0, so it correctly wraps around.
                int upperIndex = (i + 1) % size;

                // The index for the diagonal "below" the main one.
                // (0 - 1 + 16) % 16 = 15, so it correctly wraps around.
                int lowerIndex = (i - 1 + size) % size;

                meas[i, diagonalIndex] = double.NaN;
                meas[i, upperIndex] = double.NaN;
                meas[i, lowerIndex] = double.NaN;
            }

            return new EITMeasurements(meas);
        }

        #region Finite Element Method Related Functions

        public FEMMesh SolveFemForward(FEMMesh mesh)
        {
            _differentialEquationSolver = DifferentialEquationSolverFactory.Create(mesh, DifferentialEquationSolver.FiniteElementMethod, NumericSolverFactory.Create(NumericSolver.SVD));

            var conductivitiyDistribution = mesh.GetConductivityDistribution();

            var electrodes = mesh.Electrodes;

            BoundaryCondition boundaryConditions = new BoundaryCondition(electrodes);

            PotentialDistribution potentialDistribution = _differentialEquationSolver.SolveForward(mesh, boundaryConditions);

            mesh.PotentialDistribution = potentialDistribution;

            // 6) Update every vertex’s .Potential from the returned dictionary
            foreach (var v in mesh.Vertices)
            {
                v.Potential = potentialDistribution.GetPotential(v.GlobalId);
            }

            // 7) (Optional) set each electrode's measured voltage = average over its nodes
            foreach (var el in electrodes)
            {
                if (el.VertexIds.Count == 0) continue;
                double sum = el.VertexIds.Sum(id => potentialDistribution.GetPotential(id));
                el.Voltage = sum / el.VertexIds.Count;
            }

            return mesh;
        }

        public FEMMesh SolveFemInverse(FEMMesh mesh, int maxIterCount, double stepSize, double regularization)
        {
            // Forward step computes the correct potential values
            FEMMesh forwardProjection = SolveFemForward(mesh);
            double[] measuredValues = forwardProjection.GetElectrodePotentials();

            Debug.WriteLine("The simulated measured values are:");
            for (int i = 0; i < measuredValues.Length; i++)
                Debug.WriteLine($"{measuredValues[i]}");

            // Initialize inverse solver
            _differentialEquationSolver = DifferentialEquationSolverFactory.Create(mesh, DifferentialEquationSolver.FiniteElementMethod, NumericSolverFactory.Create(NumericSolver.SVD));
            _errorMetric = ErrorMetricFactory.Create(ErrorMetric.L2);
            _regularizer = RegularisationFactory.Create(RegularizationTechnique.ZeroOrderTikhonov, forwardProjection, regularization);

            // 2) Initialize conductivity (σ^{0}) to homogeneous distribution
            ConductivityDistribution sigma = ConductivityDistributionFactory.CreateRandom(mesh);
            mesh.SetConductivityDistribution(sigma);
            
            // 3) Mark electrodes: 0=ground, 1=excitation
            List<Electrode> electrodes = mesh.Electrodes;
            var bc = new BoundaryCondition(electrodes);

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
                                  new BoundaryCondition(electrodes, srcDist));
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
                    Debug.WriteLine("");
                else Debug.Write($"{errors[i]:F6},");


            // 5) Update mesh ConductivityDistribution and return
            foreach (var el in mesh.Elements)
                el.Conductivity = sigma.GetConductivity(el.Id);

            return mesh;
        }
        #endregion
    }
}

