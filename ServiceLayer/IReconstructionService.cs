using System;
using System.Threading.Tasks;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.ReconstructionParameters;

namespace ServiceLayer
{
    public interface IReconstructionService
    {
        public Task<ReconstructionResult> GetReconstructionResult();
        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters);

        // --- Background reconstruction control ---
        public event EventHandler<ReconstructionResult> ReconstructionUpdated;
        public void StartBackgroundReconstruction(int maxIterationCount, double stepSize, double regularizationWeight, double excitationAmplitude);
        public void PauseBackgroundReconstruction();
        public void ResumeBackgroundReconstruction();
        public void StopBackgroundReconstruction();
        public Task<ReconstructionResult?> StepReconstructionAsync();

        // --- LBM Reconstruction ---

        public PotentialDistribution SolveLbmForward();
        public ReconstructionResult SolveLbmInverse(int maxIterationCount);
        public EITMeasurement SimulateLbmMeasurements(LBMMesh mesh, double excitaionAmplitude);

        // --- FEM Reconstruction

        public FEMMesh SolveFemForward(FEMMesh mesh);
        public FEMMesh SolveFemInverse(FEMMesh mesh, int maxIterCount, double stepSize, double regularization);
        public ReconstructionResult InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize);
        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude);

        // --- Graph-based Reconstruction ---
        /// <summary>
        ///     Wrapper for the graph-based forward solve.  Converts the mesh to
        ///     a resistor network and evaluates the Complete Electrode Model on
        ///     that graph.
        /// </summary>
        /// <param name="mesh">Mesh to be solved.</param>
        /// <returns>Mesh with updated potentials.</returns>
        public FEMMesh SolveGraphForward(FEMMesh mesh);

        /// <summary>
        ///     Performs a single graph-based inverse iteration driven by the
        ///     mismatch between simulated and measured electrode data.
        /// </summary>
        /// <param name="mesh">Mesh whose conductivities are updated.</param>
        /// <param name="measurement">Measured electrode potentials.</param>
        /// <param name="boundaryCondition">Applied current pattern.</param>
        /// <param name="stepSize">Gradient-descent step size.</param>
        /// <returns>Reconstruction result after updating the mesh.</returns>
        public ReconstructionResult InverseSolveStepGraph(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize);
    }
}
