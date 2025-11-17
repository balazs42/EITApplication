using System.Collections.Generic;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

namespace ServiceLayer
{
    public interface IMeasurementService
    {
        void Initialize(IDiscretization discretization,
                        EITReconstructionParameters parameters,
                        DrivePattern drivePattern,
                        Func<IDifferentialEquationSolver?> solverAccessor,
                        ConductivityDistribution? measurementConductivity = null);

        void SyncMeasurementSource();
        void EnsureMeasurements(double excitationAmplitude);
        double[] GetMeasurementForStep(int stepIndex);
        IReadOnlyList<double[]> GetAllMeasurements();
        double[] PrepareMeasurementFrame(double[] measurement, IList<Electrode> electrodes);

        int FramesPerCycle { get; }
        double? RealMeasurementAmplitude { get; }
        ElectrodeMeasurementSetup MeasurementSetup { get; }
        MeasurementPattern? CurrentPattern { get; }
        bool UsePotentialDifferences { get; }
    }
}
