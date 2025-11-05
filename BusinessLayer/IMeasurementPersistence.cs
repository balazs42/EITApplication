using Utility.Classes.Measurement;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes;

namespace BusinessLayer
{
    /// <summary>
    /// Provides low-level measurement generation utilities used prior to running
    /// reconstruction.  Implementations encapsulate the heavy lifting required
    /// to produce simulated electrode measurements for FEM/LBM discretizations.
    /// </summary>
    public interface IMeasurementPersistence
    {
        /// <summary>
        /// Simulates a full drive-pattern cycle on the provided FEM mesh.
        /// </summary>
        /// <param name="mesh">Finite element mesh to simulate on.</param>
        /// <param name="excitationAmplitude">Excitation current amplitude.</param>
        /// <param name="drivePattern">Electrode drive pattern used in the cycle.</param>
        /// <param name="usePotentialDifferences">Whether potentials should be represented as differences.</param>
        /// <param name="solver">Differential equation solver configured for the reconstruction session.</param>
        /// <param name="measurementSetup">Current electrode measurement setup (active/non-active).</param>
        /// <param name="virtualSettings">Virtual electrode options currently active.</param>
        /// <returns>Simulated measurement frames and the inferred metadata.</returns>
        MeasurementSimulationResult SimulateFemMeasurements(FEMMesh mesh,
                                                            double excitationAmplitude,
                                                            DrivePattern drivePattern,
                                                            bool usePotentialDifferences,
                                                            IDifferentialEquationSolver solver,
                                                            ElectrodeMeasurementSetup measurementSetup,
                                                            VirtualElectrodeSettings virtualSettings);

        /// <summary>
        /// Simulates a full drive-pattern cycle on the provided LBM grid.
        /// </summary>
        /// <param name="grid">Lattice Boltzmann grid to simulate on.</param>
        /// <param name="excitationAmplitude">Excitation current amplitude.</param>
        /// <param name="drivePattern">Electrode drive pattern used in the cycle.</param>
        /// <param name="usePotentialDifferences">Whether potentials should be represented as differences.</param>
        /// <param name="solver">Differential equation solver configured for the reconstruction session.</param>
        /// <param name="measurementSetup">Current electrode measurement setup (active/non-active).</param>
        /// <param name="virtualSettings">Virtual electrode options currently active.</param>
        /// <returns>Simulated measurement frames and the inferred metadata.</returns>
        MeasurementSimulationResult SimulateLbmMeasurements(LBMGrid grid,
                                                            double excitationAmplitude,
                                                            DrivePattern drivePattern,
                                                            bool usePotentialDifferences,
                                                            IDifferentialEquationSolver solver,
                                                            ElectrodeMeasurementSetup measurementSetup,
                                                            VirtualElectrodeSettings virtualSettings);
    }

    /// <summary>
    /// Bundles measurement frames with metadata inferred during simulation.
    /// </summary>
    /// <param name="Frames">Simulated measurement frames.</param>
    /// <param name="Amplitude">Optional amplitude associated with the frames.</param>
    /// <param name="Pattern">Measurement pattern describing channel ordering.</param>
    /// <param name="MeasurementSetup">Whether driven electrodes are included in the frames.</param>
    public sealed record MeasurementSimulationResult(List<double[]> Frames,
                                                      double? Amplitude,
                                                      MeasurementPattern? Pattern,
                                                      ElectrodeMeasurementSetup MeasurementSetup);
}
