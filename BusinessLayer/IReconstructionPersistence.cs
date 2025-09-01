using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.ReconstructionParameters;

namespace BusinessLayer
{
    public interface IReconstructionPersistence
    {
        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters);

        // --- LBM Reconstruction ---
        public PotentialDistribution SolveLbmForward();
        public ReconstructionResult SolveLbmInverse(int maxIterationCount);
        public EITMeasurement SimulateLbmMeasurements(LBMMesh mesh, double excitationAmplitude);

        // --- FEM Reconstruction --- 
        public FEMMesh SolveFemForward(FEMMesh mesh);
        public FEMMesh SolveFemInverse(FEMMesh mesh, int maxIterCount, double stepSize, double regularization);
        public ReconstructionResult InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize);
        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude);

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
    }
}
