using BusinessLayer;
using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;
using Utility.Logger;

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
                return _reconstructionPersistence.InverseSolveStepFem(mesh, measurement, boundaryCondition, stepSize);
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
    }
}
