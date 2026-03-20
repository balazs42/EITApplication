using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

namespace ServiceLayer
{
    public interface IMeasurementService
    {
        void Initialize(IDiscretization discretization,
                        ReconstructionRuntimeContext parameters,
                        DrivePattern drivePattern,
                        Func<IDifferentialEquationSolver?> solverAccessor,
                        ConductivityDistribution? measurementConductivity = null);

        void SyncMeasurementSource();
        void EnsureMeasurements(double excitationAmplitude);
        double[] GetMeasurementForStep(int stepIndex);
        IReadOnlyList<double[]> GetAllMeasurements();
        double[] PrepareMeasurementFrame(double[] measurement, IList<Electrode> electrodes, int stepIndex = 0);
        MeasurementStepContext BuildStepContext(IList<Electrode> electrodes, double[] frame, int stepIndex);

        int FramesPerCycle { get; }
        double? RealMeasurementAmplitude { get; }
        ElectrodeMeasurementSetup MeasurementSetup { get; }
        MeasurementPattern? CurrentPattern { get; }
        bool UsePotentialDifferences { get; }
    }
}
