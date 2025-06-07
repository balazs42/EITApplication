using Utility.Classes;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Models;
using Utility.Classes.Factories;

namespace BusinessLayer
{
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        private InverseModel _inverseModel;

        // TODO: Implement initialization of these variables
        private EITMeasurement _measurementData;
        private Mesh _initialMesh;
        private ConductivityDistribution _initialConductivityDistribution;

        public async Task<ReconstructionResult> GetReconstructionResult()
        {
            if (_inverseModel == null) throw new InvalidOperationException();

            /* supply real data, mesh, and initial σ */
            var result = await Task.Run(() => 
                _inverseModel.Solve( _initialConductivityDistribution, _measurementData, 50)            
            );

            ReconstructionResult reconstructionResult = new ReconstructionResult(_initialMesh, result );
            return reconstructionResult;
        }

        public void StartReconstruction(EITReconstructionParameters p)
        {
             
        }
    }
}
