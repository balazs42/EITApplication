using BusinessLayer;
using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.ReconstructionParameters;
using Utility.Logger;

using Workspace = Utility.Classes.Application.Workspace;

namespace ServiceLayer
{
    public class ReconstructionService : IReconstructionService
    {
        private readonly IReconstructionPersistence _reconstructionPersistence;
        private readonly ILogger _logger;

        public ReconstructionService(IReconstructionPersistence reconstructionPersistence, ILogger logger)
        {
            _reconstructionPersistence = reconstructionPersistence;
            _logger = logger;
        }

        public async Task<ReconstructionResult> GetReconstructionResult()
        {
            try
            {
                return await _reconstructionPersistence.GetReconstructionResult();
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public PotentialDistribution SolveLbmForward()
        {
            try
            {
                return _reconstructionPersistence.SolveLbmForward();
            }
            catch (Exception ex) 
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public ReconstructionResult SolveLbmInverse(int maxIterationCount)
        {
            try
            {
                ReconstructionResult reconstructionResult = _reconstructionPersistence.SolveLbmInverse(maxIterationCount);

                Workspace.AddReconstructionResultToWorkspace(reconstructionResult);

                return reconstructionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public EITMeasurement SimulateLbmMeasurements(LBMMesh mesh, double excitaionAmplitude)
        {
            try
            {
                return _reconstructionPersistence.SimulateLbmMeasurements(mesh, excitaionAmplitude);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters)
        {
            try
            {
                _reconstructionPersistence.InitializeReconstruction(mesh, parameters);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public FEMMesh SolveFemForward(FEMMesh mesh)
        {
            try
            {
                return _reconstructionPersistence.SolveFemForward(mesh);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public FEMMesh SolveFemInverse(FEMMesh mesh, int maxIterCount, double stepSize, double regularization)
        {
            try
            {
                return _reconstructionPersistence.SolveFemInverse(mesh, maxIterCount, stepSize, regularization);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public ReconstructionResult InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize)
        {
            try
            {
                ReconstructionResult reconstructionResult = _reconstructionPersistence.InverseSolveStepFem(mesh, measurement, boundaryCondition, stepSize);

                Workspace.AddReconstructionResultToWorkspace(reconstructionResult);

                return reconstructionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude)
        {
            try
            {
                return _reconstructionPersistence.SimulateFemMeasurements(mesh, excitationAmplitude);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        /// <summary>
        ///     Delegates a graph-based forward solve to the persistence layer.
        ///     Internally, the mesh is converted to a resistor network and the
        ///     discrete Laplace equation with CEM boundary conditions is
        ///     solved.
        /// </summary>
        /// <param name="mesh">Mesh to be solved.</param>
        /// <returns>Mesh carrying the predicted potentials.</returns>
        public FEMMesh SolveGraphForward(FEMMesh mesh)
        {
            try
            {
                return _reconstructionPersistence.SolveGraphForward(mesh);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        /// <summary>
        ///     Executes a single graph-based inverse update.  The measured and
        ///     simulated electrode potentials define an adjoint load whose
        ///     solution yields a conductance gradient; a step is taken along
        ///     this gradient on the underlying network.
        /// </summary>
        /// <param name="mesh">Mesh whose conductivities will be updated.</param>
        /// <param name="measurement">Measured electrode potentials.</param>
        /// <param name="boundaryCondition">Applied current pattern.</param>
        /// <param name="stepSize">Gradient descent step size.</param>
        /// <returns>Reconstruction result containing updated fields.</returns>
        public ReconstructionResult InverseSolveStepGraph(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize)
        {
            try
            {
                ReconstructionResult reconstructionResult = _reconstructionPersistence.InverseSolveStepGraph(mesh, measurement, boundaryCondition, stepSize);

                Workspace.AddReconstructionResultToWorkspace(reconstructionResult);

                return reconstructionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }
    }
}
