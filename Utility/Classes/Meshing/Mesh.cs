using Utility.Classes.Meshing;

namespace Utility.Classes
{
    /// <summary>
    /// Basic interface for a Mesh type later on. This helps the meshes to be a generic type.
    /// </summary>
    public interface IMesh
    {
        public void LogMesh();
        public ConductivityDistribution GetConductivityDistribution();
        public PotentialDistribution GetPotentialDistribution();
        public Mesh GetMesh();
        public List<Electrode> GetElectrodes();
        public List<Vertex> GetVertices();
        public List<MeshElement> GetElements();
        public double[] GetElectrodePotentials();
        public List<Vertex> GetElectrodeVertices();
    }

    /// <summary>
    /// All mesh types have to inherit from ths Mesh abstract class. 
    /// This implements basic mesh functionality, like holding vertex, electrode, etc. data.
    /// </summary>
    public abstract class Mesh : IMesh
    {
        public List<Vertex> Vertices { get; protected set; }
        public List<MeshElement> Elements { get; protected set; }
        public List<Electrode> Electrodes { get; set; }
        public ConductivityDistribution ConductivityDistribution { get; set; }
        public PotentialDistribution PotentialDistribution { get; set; }

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
        public List<Electrode> GetElectrodes() => Electrodes;
        public List<Vertex> GetVertices() => Vertices;
        public List<MeshElement> GetElements() => Elements;

        /// <summary>
        /// Finds all electrode nodes, and extracts the potential values of the PotentialDistributon.
        /// </summary>
        /// <returns>The array of electrode potentials.</returns>
        public abstract double[] GetElectrodePotentials();

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
