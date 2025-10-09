using BusinessLayer;
using System;
using System.Diagnostics;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
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
        private IDiscretization? _discretization;
        private CancellationTokenSource? _cts;
        private Task? _backgroundTask;
        private bool _isPaused;
        private int _maxIterationCount;
        private int _currentIteration;
        private double _stepSize;
        private double _regularizationWeight;
        private double _excitationAmplitude;
        private List<double[]> _simulatedMeasurements = [];
        private int _simMeasurementIndex;
        private List<ReconstructionFrame> _currentCycleFrames = [];
        private ConductivityDistribution? _originalSigma;
        private ConductivityDistribution? _initialSigma;
        private int _framesPerCycle;
        private MeasurementNoiseType _measurementNoiseType = MeasurementNoiseType.None;
        private double _measurementNoiseAmplitude;
        private DrivePattern _drivePattern = DrivePattern.Adjecent;
        private IDrivePatternStrategy _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent);
        private readonly Random _noiseRandom = new();
        private bool _useOmpParallelization;

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

        public PotentialDistribution ForwardSolveStepLbmCuda()
        {
            try
            {
                Workspace.AddLogMessage("Reconstruction Service", "Performing LBM Forward Solve (CUDA).");

                return _reconstructionPersistence.ForwardSolveStepLbmCuda();
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

        public EITMeasurement SimulateLbmMeasurements(LBMGrid mesh, double excitaionAmplitude)
        {
            try
            {
                var parameters = Workspace.GetReconstructionParameters();
                var measurement = _reconstructionPersistence.SimulateLbmMeasurements(mesh, excitaionAmplitude, parameters.DrivePattern);
                var noisyFrames = CloneMeasurementsWithNoise(measurement.Frames,
                    parameters.MeasurementNoiseType,
                    parameters.MeasurementNoiseAmplitude);

                var noisyMeasurement = new EITMeasurement(noisyFrames,
                    measurement.CurrentAmplitude ?? excitaionAmplitude)
                {
                    FrameSize = measurement.FrameSize
                };

                return noisyMeasurement;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public void InitializeReconstruction(IDiscretization discretization, EITReconstructionParameters parameters, bool reinit)
        {
            try
            {
                Workspace.AddLogMessage("Reconstruction Service", "Reconstruction initialization started with the specified EITReconstructionParameters object.");
                Workspace.SetDiscretization(discretization);
                _discretization = discretization;
                _simulatedMeasurements.Clear();
                _simMeasurementIndex = 0;
                _currentCycleFrames.Clear();

                Workspace.ClearReconstructionFrames();

                Workspace.SetReconstructionResults(new List<ReconstructionResult>());

                _originalSigma = Workspace.GetOriginalDiscretization()?.GetConductivityDistribution()
                                 ?? discretization.DeepCopy().GetConductivityDistribution();
                _initialSigma = Workspace.GetInitialDiscretization()?.GetConductivityDistribution() ?? ConductivityDistributionFactory.CreateInitialDistribution(discretization, parameters.InitialDistributionType);
                discretization.SetConductivityDistribution(_initialSigma);

                Workspace.SetOriginalConductivityDistribution(_originalSigma);

                _reconstructionPersistence.SetConductivityDistributions(_originalSigma, _initialSigma);
                _reconstructionPersistence.InitializeReconstruction(discretization, parameters, reinit);

                _drivePattern = parameters.DrivePattern;
                _drivePatternStrategy = DrivePatternStrategyProvider.GetStrategy(_drivePattern);
                _useOmpParallelization = parameters.UseOmpParallelization;

                var electrodeCount = discretization.GetElectrodes().Count;
                _framesPerCycle = electrodeCount > 0 ? Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount)) : 1;

                _measurementNoiseType = parameters.MeasurementNoiseType;
                _measurementNoiseAmplitude = parameters.MeasurementNoiseAmplitude;
                if (_measurementNoiseType == MeasurementNoiseType.None || Math.Abs(_measurementNoiseAmplitude) <= double.Epsilon)
                    Workspace.AddLogMessage("Reconstruction Service", "Measurement noise disabled.");
                else
                {
                    double linearAmplitude = _measurementNoiseAmplitude < 0.0
                        ? Math.Pow(10.0, _measurementNoiseAmplitude / 20.0)
                        : Math.Abs(_measurementNoiseAmplitude);

                    string amplitudeDescriptor = _measurementNoiseAmplitude < 0.0
                        ? string.Format("{0:G4} dB -> {1:G4}", _measurementNoiseAmplitude, linearAmplitude)
                        : linearAmplitude.ToString("G4");

                    Workspace.AddLogMessage("Reconstruction Service",
                        $"Measurement noise enabled: {_measurementNoiseType} (amplitude {amplitudeDescriptor}).");
                }
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

        public ReconstructionFrame InverseSolveStepLbmCuda(LBMGrid mesh, double[] measurement, LBMBoundaryCondition boundaryCondition)
        {
            try
            {
                var frame = _reconstructionPersistence.InverseSolveStepLbmCuda(mesh, boundaryCondition, measurement);

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
                var parameters = Workspace.GetReconstructionParameters();
                var frames = _reconstructionPersistence.SimulateFemMeasurements(mesh, excitationAmplitude, parameters.DrivePattern);
                return CloneMeasurementsWithNoise(frames,
                    parameters.MeasurementNoiseType,
                    parameters.MeasurementNoiseAmplitude);
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
        private (int ExcitationIndex, int GroundIndex) GetDrivePatternPair(int electrodeCount, int stepIndex)
        {
            if (electrodeCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(electrodeCount), "Electrode count must be positive.");

            int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount));
            var (excitation, ground) = _drivePatternStrategy.GetElectrodePair(electrodeCount, stepIndex % cycleLength);
            return (excitation, ground);
        }

        private void ApplyDrivePatternToElectrodes<TElectrode>(IList<TElectrode> electrodes, double excitationAmplitude, int stepIndex)
            where TElectrode : Electrode
        {
            if (electrodes.Count == 0)
                return;

            foreach (var el in electrodes)
            {
                el.Current = 0.0;
                el.IsExcitation = false;
                el.IsGround = false;
                el.IsMeasuring = true;
                el.Potential = 0.0;
            }

            var (excitationIndex, groundIndex) = GetDrivePatternPair(electrodes.Count, stepIndex);

            var excitation = electrodes[excitationIndex];
            excitation.IsExcitation = true;
            excitation.IsMeasuring = false;

            if (electrodes[0] is FEMElectrode)
                excitation.Current = excitationAmplitude;
            else excitation.Current = excitationAmplitude + 3.0;

            var ground = electrodes[groundIndex];
            ground.IsGround = true;
            ground.IsMeasuring = false;

            if (electrodes[0] is FEMElectrode)            
                ground.Current = -excitationAmplitude;
            else ground.Current = (-excitationAmplitude + 3.0);
        }

        private void EnsureSimulatedMeasurements()
        {
            if (_simulatedMeasurements.Count > 0)
                return;

            var original = Workspace.GetOriginalDiscretization()
                           ?? throw new NullReferenceException("Original mesh not set.");

            if (_discretization is FEMMesh && original is FEMMesh femOrig)
            {
                var frames = _reconstructionPersistence.SimulateFemMeasurements(femOrig, _excitationAmplitude, _drivePattern);
                _simulatedMeasurements = CloneMeasurementsWithNoise(frames, _measurementNoiseType, _measurementNoiseAmplitude);
            }
            else if (_discretization is LBMGrid && original is LBMGrid lbmOrig)
            {
                var measurement = _reconstructionPersistence.SimulateLbmMeasurements(lbmOrig, _excitationAmplitude, _drivePattern);
                _simulatedMeasurements = CloneMeasurementsWithNoise(measurement.Frames, _measurementNoiseType, _measurementNoiseAmplitude);
            }
            else
            {
                throw new InvalidOperationException("Mesh type mismatch between original and reconstruction meshes.");
            }
        }

        private List<double[]> CloneMeasurementsWithNoise(IEnumerable<double[]> measurements,
            MeasurementNoiseType noiseType,
            double noiseAmplitude)
        {
            var clones = measurements.Select(frame => (double[])frame.Clone()).ToList();
            ApplyMeasurementNoise(clones, noiseType, noiseAmplitude);
            return clones;
        }

        private void ApplyMeasurementNoise(List<double[]> measurements, MeasurementNoiseType noiseType, double noiseAmplitude)
        {
            if (measurements.Count == 0 || noiseType == MeasurementNoiseType.None)
                return;

            bool amplitudeInDecibels = noiseAmplitude < 0.0;
            double amplitude = amplitudeInDecibels
                ? Math.Pow(10.0, noiseAmplitude / 20.0)
                : Math.Abs(noiseAmplitude);
            if (amplitude <= double.Epsilon)
                return;

            foreach (var frame in measurements)
            {
                for (int i = 0; i < frame.Length; i++)
                {
                    double noise = noiseType switch
                    {
                        MeasurementNoiseType.Gaussian => NextGaussian(0.0, amplitude),
                        MeasurementNoiseType.Uniform => (_noiseRandom.NextDouble() * 2.0 - 1.0) * amplitude,
                        _ => 0.0
                    };

                    frame[i] += noise;
                }
            }
        }

        private double NextGaussian(double mean, double stdDev)
        {
            double u1 = 1.0 - _noiseRandom.NextDouble();
            double u2 = 1.0 - _noiseRandom.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return mean + stdDev * randStdNormal;
        }

        private ReconstructionFrame? PerformInverseStep()
        {
            if (_discretization is FEMMesh femMesh)
            {
                if (_useOmpParallelization)
                {
                    var result = RunFemIterationsWithOmp(1);
                    return result?.Frames.LastOrDefault();
                }

                EnsureSimulatedMeasurements();

                // Select the measurement frame corresponding to the current
                // excitation pair.  Electrode roles must be reconfigured for
                // each frame so that the boundary condition reflects the
                // rotating drive pattern.
                int electrodeCount = femMesh.GetElectrodes().Count;
                int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount));
                int stepIndex = _simMeasurementIndex % cycleLength;
                var measurement = _simulatedMeasurements[stepIndex];

                var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                ApplyDrivePatternToElectrodes(electrodes, _excitationAmplitude, stepIndex);

                var bc = new FEMBoundaryCondition(electrodes);

                var frame = _reconstructionPersistence.Step(measurement, bc, _stepSize, _regularizationWeight);
                _simMeasurementIndex++;
                Workspace.AddReconstructionFrameToWorkspace(frame);
                _currentCycleFrames.Add(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);

                if (_simMeasurementIndex % _framesPerCycle == 0)
                {
                    var result = new ReconstructionResult(_discretization!.GetDiscretization(),
                                                          _originalSigma!,
                                                          _initialSigma!,
                                                          _discretization!.GetConductivityDistribution(),
                                                          [.. _currentCycleFrames]);
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    _currentIteration++;
                }

                return frame;
            }
            else if (_discretization is LBMGrid lbmGrid)
            {
                EnsureSimulatedMeasurements();

                var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
                int electrodeCount = electrodes.Count;
                int cycleLength = Math.Max(1, _drivePatternStrategy.GetCycleLength(electrodeCount));
                int stepIndex = _simMeasurementIndex % cycleLength;
                ApplyDrivePatternToElectrodes(electrodes, _excitationAmplitude, stepIndex);

                var bc = new LBMBoundaryCondition(electrodes);
                double[] measurement = _simulatedMeasurements[stepIndex];
                var frame = _reconstructionPersistence.Step(measurement, bc, _stepSize, _regularizationWeight);
                _simMeasurementIndex++;
                Workspace.AddReconstructionFrameToWorkspace(frame);
                _currentCycleFrames.Add(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);

                if (_simMeasurementIndex % _framesPerCycle == 0)
                {
                    var result = new ReconstructionResult(_discretization!.GetDiscretization(),
                                                          _originalSigma!,
                                                          _initialSigma!, 
                                                          _discretization!.GetConductivityDistribution(),
                                                          [.. _currentCycleFrames]);
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
                if (_discretization is FEMMesh femMesh)
                {
                    if (_useOmpParallelization)
                        return RunFemIterationsWithOmp(1);

                    EnsureSimulatedMeasurements();

                    var electrodes = femMesh.GetElectrodes().Cast<FEMElectrode>().ToList();
                    int electrodeCount = electrodes.Count;

                    for (int i = 0; i < _simulatedMeasurements.Count; i++)
                    {
                        ApplyDrivePatternToElectrodes(electrodes, _excitationAmplitude, i);

                        var bc = new FEMBoundaryCondition(electrodes);
                        var measurement = _simulatedMeasurements[i];

                        var frame = _reconstructionPersistence.Step(measurement, bc, _stepSize, _regularizationWeight);

                        Workspace.AddReconstructionFrameToWorkspace(frame);
                        _currentCycleFrames.Add(frame);
                        ReconstructionFrameUpdated?.Invoke(this, frame);
                    }

                    // accumulate gradient and update conductivity distribution
                    var frameCount = _currentCycleFrames.Count;
                    var prevSigma = _discretization!.GetConductivityDistribution();
                    var accumGrad = new Dictionary<int, double>();
                    foreach (var frame in _currentCycleFrames)
                        foreach (var kvp in frame.ConductivityGradient.Conductivities)
                            accumGrad[kvp.Key] = accumGrad.TryGetValue(kvp.Key, out var g) ? g + kvp.Value : kvp.Value;

                    var newSigmaDict = prevSigma.Conductivities.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value + (accumGrad.TryGetValue(kvp.Key, out var g) ? g / frameCount : 0.0));

                    // Clip conductivity values that would fall below 0.0
                    foreach (var kvp in newSigmaDict)
                        if (kvp.Value < 0.0)
                            newSigmaDict[kvp.Key] = 0.0;

                    var newSigma = new ConductivityDistribution(newSigmaDict);
                    _discretization.SetConductivityDistribution(newSigma);
                    _initialSigma = newSigma;
                    _reconstructionPersistence.SetConductivityDistributions(_originalSigma!, _initialSigma!);

                    var result = new ReconstructionResult(_discretization!.GetDiscretization(),
                                                          _originalSigma!,
                                                          prevSigma, 
                                                          newSigma, 
                                                          [.. _currentCycleFrames]);
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    return result;
                }
                else if (_discretization is LBMGrid lbmGrid)
                {
                    EnsureSimulatedMeasurements();

                    for (int i = 0; i < _simulatedMeasurements.Count; i++)
                    {
                        var electrodes = lbmGrid.GetElectrodes().Cast<LBMElectrode>().ToList();
                        ApplyDrivePatternToElectrodes(electrodes, _excitationAmplitude, i);
                        var bc = new LBMBoundaryCondition(electrodes);

                        var measurement = _simulatedMeasurements[i];
                        var frame = _reconstructionPersistence.Step(measurement, bc, _stepSize, _regularizationWeight);

                        Workspace.AddReconstructionFrameToWorkspace(frame);
                        _currentCycleFrames.Add(frame);
                        ReconstructionFrameUpdated?.Invoke(this, frame);

                        lbmGrid.ShiftExcitationElectrodes(_drivePattern);
                    }

                    var frameCount = _currentCycleFrames.Count;
                    var prevSigma = _discretization!.GetConductivityDistribution();
                    var accumGrad = new Dictionary<int, double>();
                    foreach (var frame in _currentCycleFrames)
                        foreach (var kvp in frame.ConductivityGradient.Conductivities)
                            accumGrad[kvp.Key] = accumGrad.TryGetValue(kvp.Key, out var g) ? g + kvp.Value : kvp.Value;

                    var newSigmaDict = prevSigma.Conductivities.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value + (accumGrad.TryGetValue(kvp.Key, out var g) ? g / frameCount : 0.0));
                    var newSigma = new ConductivityDistribution(newSigmaDict);
                    _discretization.SetConductivityDistribution(newSigma);
                    _initialSigma = newSigma;
                    _reconstructionPersistence.SetConductivityDistributions(_originalSigma!, _initialSigma!);

                    var result = new ReconstructionResult(_discretization!.GetDiscretization(), 
                                                          _originalSigma!,
                                                          prevSigma, 
                                                          newSigma, 
                                                          [.. _currentCycleFrames]);
                    Workspace.AddReconstructionResultToWorkspace(result);
                    ReconstructionUpdated?.Invoke(this, result);
                    _currentCycleFrames.Clear();
                    return result;
                }

                return null;
            });
        }

        private ReconstructionResult? RunFemIterationsWithOmp(int iterationCount)
        {
            if (iterationCount <= 0)
                return null;

            _reconstructionPersistence.Run(iterationCount, _stepSize, _regularizationWeight);
            var result = _reconstructionPersistence.Stop();

            if (result.Frames.Count == 0)
            {
                _currentIteration += iterationCount;
                return result;
            }

            foreach (var frame in result.Frames)
            {
                Workspace.AddReconstructionFrameToWorkspace(frame);
                ReconstructionFrameUpdated?.Invoke(this, frame);
            }

            Workspace.AddReconstructionResultToWorkspace(result);
            ReconstructionUpdated?.Invoke(this, result);

            if (_originalSigma != null)
            {
                _initialSigma = result.ReconstructedConductivityDistribution;
                _reconstructionPersistence.SetConductivityDistributions(_originalSigma, _initialSigma);
            }

            _discretization?.SetConductivityDistribution(result.ReconstructedConductivityDistribution);

            _currentCycleFrames.Clear();
            _simMeasurementIndex = 0;
            _currentIteration += iterationCount;

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
                Workspace.SetReconstructionFrames([.. frames.SelectMany(r => r.Frames)]);
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
        public ReconstructionResult InverseSolveLbmCuda(int maxIterationCount,
                                                        double gradientStepSize,
                                                        double regularizationWeight,
                                                        double excitationAmplitude,
                                                        double tolerance = 1e-6)
        {
            try
            {
                ReconstructionResult reconstructionResult =
                    _reconstructionPersistence.InverseSolveLbmCuda(maxIterationCount,
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
    }
}