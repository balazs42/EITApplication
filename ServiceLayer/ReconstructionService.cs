using BusinessLayer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        // Background reconstruction state
        private IMesh? _mesh;
        private CancellationTokenSource? _cts;
        private Task? _backgroundTask;
        private bool _isPaused;
        private int _maxIterationCount;
        private int _currentIteration;
        private double _stepSize;
        private double _regularizationWeight;
        private double _excitationAmplitude;
        private List<double[]> _simulatedMeasurements = new();
        private int _simMeasurementIndex;

        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;

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
                Workspace.AddLogMessage("Reconstruction Service", "Performing LBM Forward Solve.");

                return _reconstructionPersistence.SolveLbmForward();
            }
            catch (Exception ex) 
            {
                Workspace.AddErrorMessage(ex.Message);
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
                Workspace.AddLogMessage("Reconstruction Service", "Reconstruction initialization started with the specified EITReconstructionParameters object.");
                _mesh = mesh;
                _simulatedMeasurements.Clear();
                _simMeasurementIndex = 0;
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
                Workspace.AddLogMessage("Reconstruction Service", "Performing FEM forward solve.");

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

        #region Background reconstruction

        private ReconstructionResult? PerformInverseStep()
        {
            if (_mesh is FEMMesh femMesh)
            {
                if (_simulatedMeasurements.Count == 0)
                {
                    _simulatedMeasurements = _reconstructionPersistence.SimulateFemMeasurements(femMesh, _excitationAmplitude);
                }

                var measurement = _simulatedMeasurements[_simMeasurementIndex % _simulatedMeasurements.Count];
                var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                var bc = new FEMBoundaryCondition(electrodes);

                var result = InverseSolveStepFem(femMesh, measurement, bc, _stepSize);
                _simMeasurementIndex++;
                return result;
            }
            else if (_mesh is LBMMesh)
            {
                return SolveLbmInverse(1);
            }

            return null;
        }

        private async Task RunLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _currentIteration < _maxIterationCount)
            {
                if (_isPaused)
                {
                    await Task.Delay(100, token);
                    continue;
                }

                var result = PerformInverseStep();
                if (result != null)
                {
                    _currentIteration++;
                    ReconstructionUpdated?.Invoke(this, result);
                }

                await Task.Yield();
            }
        }

        public void StartBackgroundReconstruction(int maxIterationCount, double stepSize, double regularizationWeight, double excitationAmplitude)
        {
            _maxIterationCount = maxIterationCount;
            _stepSize = stepSize;
            _regularizationWeight = regularizationWeight;
            _excitationAmplitude = excitationAmplitude;
            _currentIteration = 0;
            _isPaused = false;
            _cts = new CancellationTokenSource();
            _backgroundTask = Task.Run(() => RunLoop(_cts.Token));
        }

        public void PauseBackgroundReconstruction() => _isPaused = true;

        public void ResumeBackgroundReconstruction() => _isPaused = false;

        public void StopBackgroundReconstruction()
        {
            _cts?.Cancel();
            _backgroundTask = null;
            _isPaused = false;
        }

        public async Task<ReconstructionResult?> StepReconstructionAsync()
        {
            var result = await Task.Run(PerformInverseStep);
            if (result != null)
            {
                _currentIteration++;
                ReconstructionUpdated?.Invoke(this, result);
            }
            return result;
        }

        #endregion

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
