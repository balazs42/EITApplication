using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.ReconstructionParameters;

namespace BusinessLayer
{
    public interface IReconstructionPersistence
    {
        void SetConductivityDistributions(ConductivityDistribution original, ConductivityDistribution initial);
        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters, bool reinit);

        public ReconstructionFrame Step(double[] measurement, BoundaryCondition boundaryCondition, double gradientStepSize, double redularizationStepSize);
        public void Run(int maxIterationCount, double gradientStepSize, double redularizationStepSize);
        public ReconstructionResult Stop();

        // --- Forward Solve Functions ---
        public PotentialDistribution ForwardSolveStepFem();
        public PotentialDistribution ForwardSolveStepLbm();

        // --- Inverse Solve Functions ---
        public ReconstructionFrame InverseSolveStepFem(FEMMesh mesh, FEMBoundaryCondition bc, double[] currentMeasurement, double gradientStepSize);
        public ReconstructionFrame InverseSolveStepLbm(LBMMesh mesh, LBMBoundaryCondition bc, double[] currentMeasurement);

        public ReconstructionResult InverseSolveFem(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6);
        public ReconstructionResult InverseSolveLbm(int maxIterationCount, double gradientStepSize, double redularizationStepSize, double excitationAmplitude, double tolerance = 1e-6);


        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude);
        public EITMeasurement SimulateLbmMeasurements(LBMMesh mesh, double excitationAmplitude);

        // --- Graph-based Reconstruction ---
        /// <summary>
        ///     Performs a forward solve using the graph-based CEM model.  The
        ///     mesh is first converted to a graph and the discrete Laplacian is
        ///     solved to obtain electrode potentials.
        /// </summary>
        /// <param name="mesh">Finite element mesh whose boundary data is solved.</param>
        /// <returns>Mesh with updated potential distribution.</returns>
        public FEMMesh SolveGraphForward(FEMMesh mesh);

        /// <summary>
        ///     Executes one inverse step on the graph model by computing the
        ///     adjoint field and updating edge conductances.  This corresponds
        ///     to a gradient descent on
        ///     <c>‖Λ(g) - Λ<sub>meas</sub>‖²</c> with respect to the conductances
        ///     encoded in the mesh.
        /// </summary>
        /// <param name="mesh">Mesh whose conductivities will be updated.</param>
        /// <param name="measurement">Measured electrode potentials.</param>
        /// <param name="boundaryCondition">Applied current pattern.</param>
        /// <param name="stepSize">Descent step size for updating conductances.</param>
        /// <returns>Reconstruction result after the update step.</returns>
        public ReconstructionResult InverseSolveStepGraph(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize);

        // --- Persistence ---
        void SaveReconstruction(List<ReconstructionResult> frames, string name, EITReconstructionParameters parameters);
        IEnumerable<ReconstructionInfo> GetReconstructions();
        List<ReconstructionResult> LoadReconstruction(string filePath);
    }
}
