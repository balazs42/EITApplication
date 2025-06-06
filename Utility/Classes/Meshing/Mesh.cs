using Utility.Classes.Meshing;

namespace Utility.Classes
{
    public interface IMesh
    {
        public void LogMesh();
        public ConductivityDistribution GetConductivityDistribution();
        public PotentialDistribution GetPotentialDistribution();
        public Mesh GetMesh();
        public List<Vertex> GetElectrodeVertices();
    }

    public abstract class Mesh : IMesh
    {
        public List<Vertex> Vertices;
        public ConductivityDistribution ConductivityDistribution;
        public PotentialDistribution PotentialDistribution;

        public Mesh(int numVertices)
        {
            Vertices = new List<Vertex>();

            for(int i = 0; i <  numVertices; i++) 
                Vertices.Add(new Vertex(i));
        }

        public Mesh(List<Vertex> vertices)
        {
            Vertices = vertices;
        }

        public Mesh()
        {

        }

        public void LogMesh()
        {

        }

        public ConductivityDistribution GetConductivityDistribution() => ConductivityDistribution;
        public PotentialDistribution GetPotentialDistribution() => PotentialDistribution;
        public Mesh GetMesh() => this;
        public double[] GetElectrodePotentials()
        {
            // First, find all electrode vertices
            var electrodeVertices = Vertices.Where(v => v.IsElectrode)
                                           .OrderBy(v => v.ElectrodeId)
                                           .ToList();

            double[] electrodePotentials = new double[electrodeVertices.Count];

            // Direct, fast lookup using the vertex ID
            for (int i = 0; i < electrodeVertices.Count; i++)
                electrodePotentials[i] = PotentialDistribution.GetPotential(electrodeVertices[i].GlobalId);

            return electrodePotentials;
        }

        public List<Vertex> GetElectrodeVertices()
        {
            List<Vertex> electrodeVertices = [];
            foreach (Vertex v in Vertices)
                if (v.IsElectrode)
                    electrodeVertices.Add(v);

            return electrodeVertices;
        }
    }
}
