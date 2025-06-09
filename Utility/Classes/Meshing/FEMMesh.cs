namespace Utility.Classes.Meshing
{
    public class FEMMesh : Mesh
    {
        public new List<FEMElement> Elements { get; set; } = [];

        public FEMMesh(List<Vertex> vertices, List<FEMElement> elements)
        {
            this.Vertices = vertices;

            // It's important to set both the specific and the base collection
            // so that methods on the base Mesh class work correctly.
            this.Elements = elements;
            base.Elements = elements.Cast<MeshElement>().ToList();

            // Initialize with a homogeneous conductivity distribution
            this.ConductivityDistribution = PriorConductivityDistributionGenerator.GenerateHomogeneousDistribution(this, 1.0);
        }

        public FEMMesh()
        {
        }

        public override double[] GetElectrodePotentials()
        {
            throw new NotImplementedException();
        }
    }
}
