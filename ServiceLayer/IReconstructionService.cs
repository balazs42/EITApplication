using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.ReconstructionParameters;

namespace ServiceLayer
{
    public interface IReconstructionService
    {
        void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters, bool reinit);

        // --- Background reconstruction control ---
        event EventHandler<ReconstructionResult> ReconstructionUpdated;
        event EventHandler<ReconstructionFrame> ReconstructionFrameUpdated;
        void StartBackgroundReconstruction(int maxIterationCount, double stepSize, double regularizationWeight, double excitationAmplitude);
        void PauseBackgroundReconstruction();
        void ResumeBackgroundReconstruction();
        void StopBackgroundReconstruction();
        Task<ReconstructionFrame?> StepReconstructionAsync();

        Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                    double regularizationWeight,
                                                                    double excitationAmplitude);

        // --- LBM Reconstruction ---
        PotentialDistribution ForwardSolveStepLbm();
        ReconstructionResult InverseSolveLbm(int maxIterationCount,
                                             double gradientStepSize,
                                             double regularizationWeight,
                                             double excitationAmplitude,
                                             double tolerance = 1e-6);
        EITMeasurement SimulateLbmMeasurements(LBMMesh mesh, double excitaionAmplitude);

        // --- FEM Reconstruction
        PotentialDistribution ForwardSolveStepFem();
        ReconstructionResult InverseSolveFem(int maxIterationCount,
                                             double gradientStepSize,
                                             double regularizationWeight,
                                             double excitationAmplitude,
                                             double tolerance = 1e-6);
        ReconstructionFrame InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize);
        List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude);

        // --- Graph-based Reconstruction ---
        /// <summary>
        ///     Wrapper for the graph-based forward solve.  Converts the mesh to
        ///     a resistor network and evaluates the Complete Electrode Model on
        ///     that graph.
        /// </summary>
        /// <param name="mesh">Mesh to be solved.</param>
        /// <returns>Mesh with updated potentials.</returns>
        FEMMesh SolveGraphForward(FEMMesh mesh);

        /// <summary>
        ///     Performs a single graph-based inverse iteration driven by the
        ///     mismatch between simulated and measured electrode data.
        /// </summary>
        /// <param name="mesh">Mesh whose conductivities are updated.</param>
        /// <param name="measurement">Measured electrode potentials.</param>
        /// <param name="boundaryCondition">Applied current pattern.</param>
        /// <param name="stepSize">Gradient-descent step size.</param>
        /// <returns>Reconstruction result after updating the mesh.</returns>
        ReconstructionResult InverseSolveStepGraph(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize);

        // --- Persistence ---
        void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters);
        IEnumerable<ReconstructionInfo> GetReconstructions();
        List<ReconstructionResult> LoadReconstruction(string filePath);
    }
}
