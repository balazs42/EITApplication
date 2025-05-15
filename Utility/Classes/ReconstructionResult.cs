using Utility.Classes.Meshing;

namespace Utility.Classes
{
    public class ReconstructionResult
    {
        public Mesh Mesh = new FEMMesh();
        public ConductivityDistribution ConductivityDistribution;

        public ReconstructionResult(Mesh mesh, ConductivityDistribution conductivityDistribution)
        {
            Mesh = mesh;
            ConductivityDistribution = conductivityDistribution;
        }
    }
}
