using Utility.Classes;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers;

namespace BusinessLayer
{
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        private InverseSolver? _inverseSolver;

        // TODO: Implement initialization of these variables
        private EITMeasurement _measurementData;
        private Mesh _initialMesh;
        private ConductivityDistribution _initialConductivityDistribution;

        public async Task<ReconstructionResult> GetReconstructionResult()
        {
            if (_inverseSolver == null) throw new InvalidOperationException();

            /* supply real data, mesh, and initial σ */
            var result = await Task.Run(() => _inverseSolver.Reconstruct(_measurementData, 
                                                                         _initialMesh, 
                                                                         _initialConductivityDistribution));

            return result;
        }

        public void StartReconstruction(EITReconstructionParameters p)
        {
            _inverseSolver = new InverseSolver(p);
        }
    }
}
