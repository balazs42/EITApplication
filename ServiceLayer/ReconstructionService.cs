using BusinessLayer;
using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Factories;
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
        private List<ReconstructionFrame> _currentCycleFrames = new();
        private ConductivityDistribution? _originalSigma;
        private ConductivityDistribution? _initialSigma;
        private int _framesPerCycle;

        public event EventHandler<ReconstructionResult>? ReconstructionUpdated;
        public event EventHandler<ReconstructionFrame>? ReconstructionFrameUpdated;

        public ReconstructionService(IReconstructionPersistence reconstructionPersistence, ILogger logger)
        {
            _reconstructionPersistence = reconstructionPersistence;
            _logger = logger;
        }

        public PotentialDistribution ForwardSolveStepLbm()
        {
            try
            {
                Workspace.AddLogMessage("Reconstruction Service", "Performing LBM Forward Solve.");

                return _reconstructionPersistence.ForwardSolveStepLbm();
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

        public ReconstructionResult InverseSolveLbm(int maxIterationCount,
                                                    double gradientStepSize,
                                                    double regularizationWeight,
                                                    double excitationAmplitude,
                                                    double tolerance = 1e-6)
        {
            try
            {
                ReconstructionResult reconstructionResult =
                    _reconstructionPersistence.InverseSolveLbm(maxIterationCount,
                                                               gradientStepSize,
                                                               regularizationWeight,
                                                               excitationAmplitude,
                                                               tolerance);

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

        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters, bool reinit)
        {
            try
            {
                Workspace.AddLogMessage("Reconstruction Service", "Reconstruction initialization started with the specified EITReconstructionParameters object.");
                Workspace.SetMesh(mesh);
                _mesh = mesh;
                _simulatedMeasurements.Clear();
                _simMeasurementIndex = 0;
                _currentCycleFrames.Clear();

                Workspace.ClearReconstructionFrames();

                Workspace.SetReconstructionResults(new List<ReconstructionResult>());

                _originalSigma = Workspace.GetOriginalMesh()?.GetConductivityDistribution()
                                 ?? mesh.DeepCopy().GetConductivityDistribution();
                _initialSigma = Workspace.GetInitialMesh()?.GetConductivityDistribution() ?? ConductivityDistributionFactory.CreateInitialDistribution(mesh, parameters.InitialDistributionType);
                mesh.SetConductivityDistribution(_initialSigma);

                Workspace.SetOriginalConductivityDistribution(_originalSigma);

                _reconstructionPersistence.SetConductivityDistributions(_originalSigma, _initialSigma);
                _reconstructionPersistence.InitializeReconstruction(mesh, parameters, reinit);

                _framesPerCycle = (mesh.GetElectrodes().Count > 0) ? mesh.GetElectrodes().Count : 1;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public PotentialDistribution ForwardSolveStepFem()
        {
            try
            {
                Workspace.AddLogMessage("Reconstruction Service", "Performing FEM forward solve.");

                return _reconstructionPersistence.ForwardSolveStepFem();
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public ReconstructionResult InverseSolveFem(int maxIterCount,
                                                    double stepSize,
                                                    double regularizationWeight,
                                                    double excitationAmplitude,
                                                    double tolerance = 1e-6)
        {
            try
            {
                var reconstructionResult = _reconstructionPersistence.InverseSolveFem(maxIterCount,
                                                                                      stepSize,
                                                                                      regularizationWeight,
                                                                                      excitationAmplitude,
                                                                                      tolerance);

                Workspace.AddReconstructionResultToWorkspace(reconstructionResult);

                return reconstructionResult;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public ReconstructionFrame InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize)
        {
            try
            {
                var femBc = boundaryCondition as FEMBoundaryCondition
                             ?? throw new ArgumentException("Boundary condition must be FEMBoundaryCondition", nameof(boundaryCondition));

                ReconstructionFrame frame = _reconstructionPersistence.InverseSolveStepFem(mesh, femBc, measurement, stepSize);

                Workspace.AddReconstructionFrameToWorkspace(frame);

                return frame;
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

        /// <summary>
        ///     Ensure that synthetic measurements are available by simulating
        ///     them on the original mesh. The original mesh is retrieved from
        ///     the workspace and deep-copied inside the persistence layer so
        ///     that it remains unchanged.
        /// </summary>
        private void EnsureSimulatedMeasurements()
        {
            if (_simulatedMeasurements.Count > 0)
                return;

            var original = Workspace.GetOriginalMesh()
                           ?? throw new NullReferenceException("Original mesh not set.");

            if (_mesh is FEMMesh && original is FEMMesh femOrig)
            {
                _simulatedMeasurements =
                    _reconstructionPersistence.SimulateFemMeasurements(femOrig, _excitationAmplitude);
            }
            else if (_mesh is LBMMesh && original is LBMMesh lbmOrig)
            {
                _simulatedMeasurements = _reconstructionPersistence
                    .SimulateLbmMeasurements(lbmOrig, _excitationAmplitude).Frames;
            }
            else
            {
                throw new InvalidOperationException("Mesh type mismatch between original and reconstruction meshes.");
            }
        }

        private ReconstructionFrame? PerformInverseStep()
        {
            if (_mesh is FEMMesh femMesh)
            {
                EnsureSimulatedMeasurements();

                // Select the measurement frame corresponding to the current
                // excitation pair.  Electrode roles must be reconfigured for
                // each frame so that the boundary condition reflects the
                // rotating drive pattern.
                int electrodeCount = femMesh.GetElectrodes().Count;
                int exc = _simMeasurementIndex % electrodeCount;
                var measurement = _simulatedMeasurements[exc];

                var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();

                // Reset electrode state before assigning the new excitation
                // pattern.
                foreach (var el in electrodes)
                {
                    el.Current = 0.0;
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.Potential = 0.0;
                }

                electrodes[exc].IsExcitation = true;
                electrodes[exc].Current = _excitationAmplitude;
                electrodes[(exc + 1) % electrodeCount].IsGround = true;
                electrodes[(exc + 1) % electrodeCount].Current = -_excitationAmplitude;

                var bc = new FEMBoundaryCondition(electrodes);

                var frame = _reconstructionPersistence.Step(measurement, bc, _stepSize, _regularizationWeight);
                _simMeasurementIndex++;
                Workspace.AddReconstructionFrameToWorkspace(frame);
                _currentCycleFrames.Add(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);

                if (_simMeasurementIndex % _framesPerCycle == 0)
                {
                    var result = new ReconstructionResult(_mesh!.GetMesh(), _originalSigma!, _initialSigma!, _mesh!.GetConductivityDistribution(), _currentCycleFrames.ToList());
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    _currentIteration++;
                }

                return frame;
            }
            else if (_mesh is LBMMesh lbmMesh)
            {
                EnsureSimulatedMeasurements();

                var electrodes = lbmMesh.GetElectrodes().Cast<LBMElectrode>().ToList();

                foreach (var el in electrodes)
                {
                    el.Current = 0.0;
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.Potential = 0.0;
                }

                int electrodeCount = electrodes.Count;
                int exc = _simMeasurementIndex % electrodeCount;
                electrodes[exc].IsExcitation = true;
                electrodes[exc].Current = _excitationAmplitude;
                electrodes[(exc + 1) % electrodeCount].IsGround = true;
                electrodes[(exc + 1) % electrodeCount].Current = -_excitationAmplitude;

                var bc = new LBMBoundaryCondition(electrodes);
                double[] measurement = _simulatedMeasurements[exc];
                var frame = _reconstructionPersistence.Step(measurement, bc, _stepSize, _regularizationWeight);
                _simMeasurementIndex++;
                Workspace.AddReconstructionFrameToWorkspace(frame);
                _currentCycleFrames.Add(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);

                if (_simMeasurementIndex % _framesPerCycle == 0)
                {
                    var result = new ReconstructionResult(_mesh!.GetMesh(), _originalSigma!, _initialSigma!, _mesh!.GetConductivityDistribution(), _currentCycleFrames.ToList());
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    _currentIteration++;
                }

                return frame;
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

                PerformInverseStep();
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

        public async Task<ReconstructionFrame?> StepReconstructionAsync()
        {
            var frame = await Task.Run(PerformInverseStep);
            return frame;
        }

        public async Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                               double regularizationWeight,
                                                                               double excitationAmplitude)
        {
            _stepSize = stepSize;
            _regularizationWeight = regularizationWeight;
            _excitationAmplitude = excitationAmplitude;
            _currentCycleFrames.Clear();

            return await Task.Run(() =>
            {
                if (_mesh is FEMMesh femMesh)
                {
                    EnsureSimulatedMeasurements();

                    var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                    int electrodeCount = electrodes.Count;

                    for (int i = 0; i < _simulatedMeasurements.Count; i++)
                    {
                        foreach (var el in electrodes)
                        {
                            el.Current = 0.0;
                            el.IsExcitation = false;
                            el.IsGround = false;
                            el.Potential = 0.0;
                        }

                        electrodes[i % electrodeCount].IsExcitation = true;
                        electrodes[i % electrodeCount].Current = _excitationAmplitude;
                        electrodes[(i + 1) % electrodeCount].IsGround = true;
                        electrodes[(i + 1) % electrodeCount].Current = -_excitationAmplitude;

                        var bc = new FEMBoundaryCondition(electrodes);
                        var measurement = _simulatedMeasurements[i];

                        var frame = _reconstructionPersistence.Step(measurement, bc, _stepSize, _regularizationWeight);
                        Workspace.AddReconstructionFrameToWorkspace(frame);
                        _currentCycleFrames.Add(frame);
                        ReconstructionFrameUpdated?.Invoke(this, frame);
                    }

                    var result = new ReconstructionResult(_mesh!.GetMesh(), _originalSigma!, _initialSigma!, _mesh!.GetConductivityDistribution(), _currentCycleFrames.ToList());
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    return result;
                }
                else if (_mesh is LBMMesh lbmMesh)
                {
                    EnsureSimulatedMeasurements();

                    foreach (var measurement in _simulatedMeasurements)
                    {
                        var electrodes = lbmMesh.GetElectrodes().Cast<LBMElectrode>().ToList();
                        var bc = new LBMBoundaryCondition(electrodes);

                        var frame = _reconstructionPersistence.Step(measurement, bc, _stepSize, _regularizationWeight);

                        Workspace.AddReconstructionFrameToWorkspace(frame);
                        _currentCycleFrames.Add(frame);
                        ReconstructionFrameUpdated?.Invoke(this, frame);

                        lbmMesh.ShiftExcitationElectrodes(DrivePattern.Adjecent);
                    }

                    var result = new ReconstructionResult(_mesh!.GetMesh(), _originalSigma!, _initialSigma!, _mesh!.GetConductivityDistribution(), _currentCycleFrames.ToList());
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    return result;
                }

                return null;
            });
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

        // --- Persistence ---
        public void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters)
        {
            try
            {
                _reconstructionPersistence.SaveReconstruction(frames, name, parameters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public IEnumerable<ReconstructionInfo> GetReconstructions()
        {
            try
            {
                return _reconstructionPersistence.GetReconstructions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public List<ReconstructionResult> LoadReconstruction(string filePath)
        {
            try
            {
                var frames = _reconstructionPersistence.LoadReconstruction(filePath);
                Workspace.SetReconstructionResults(frames);
                Workspace.SetReconstructionFrames(frames.SelectMany(r => r.Frames).ToList());
                return frames;
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
