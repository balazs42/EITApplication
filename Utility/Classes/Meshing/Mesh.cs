using Utility.Classes.Meshing;
using Utility.Classes.Meshing.GraphMesh;

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
        public Mesh DeepCopy();
        public Graph ToGraph();
        public Mesh FromGraph();
    }

    /// <summary>
    /// All mesh types have to inherit from ths Mesh abstract class. 
    /// This implements basic mesh functionality, like holding vertex, electrode, etc. data.
    /// </summary>
    public abstract class Mesh : IMesh
    {
        public List<Vertex> Vertices { get; protected set; } = [];
        public List<MeshElement> Elements { get; protected set; } = [];
        public List<Electrode> Electrodes { get; set; } = [];
        public ConductivityDistribution ConductivityDistribution { get; set; }
        public PotentialDistribution PotentialDistribution { get; set; }

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

        public void SetConductivityDistribution(ConductivityDistribution conductivityDistribution)
        {
            if (conductivityDistribution == null || conductivityDistribution.Conductivities.Count != ConductivityDistribution.Conductivities.Count)
                throw new ArgumentOutOfRangeException("Cannot set conductivity distribution to differing size, check code!");

            var keys1 = conductivityDistribution.Conductivities.Keys.OrderBy(x => x).ToList();
            var keys2 = ConductivityDistribution.Conductivities.Keys.OrderBy(x => x).ToList();

            for (int i = 0; i < keys1.Count; i++)
                if (keys1[i] != keys2[i])
                    throw new ArgumentOutOfRangeException("Cannot set new conductivity distribution, if not all keys match!");

            ConductivityDistribution = conductivityDistribution;

            foreach (var kvp in conductivityDistribution.Conductivities)
            {
                var element = Elements.Find(x => x.Id == kvp.Key);

                if (element == null)
                    throw new NullReferenceException("Could not update LBM mesh element value, since the id of the element does not match any insatnces in the provided conducitivty distribution keys. Check code!");

                element.Conductivity = kvp.Value;
            }
        }

        public void SetPotentialDistribution(PotentialDistribution potentialDistribution)
        {
            if (potentialDistribution == null || potentialDistribution.Potentials.Count != PotentialDistribution.Potentials.Count)
                throw new ArgumentOutOfRangeException("Cannot set conductivity distribution to differing size, check code!");

            var keys1 = potentialDistribution.Potentials.Keys.OrderBy(x => x).ToList();
            var keys2 = PotentialDistribution.Potentials.Keys.OrderBy(x => x).ToList();

            for (int i = 0; i < keys1.Count; i++)
                if (keys1[i] != keys2[i])
                    throw new ArgumentOutOfRangeException("Cannot set new conductivity distribution, if not all keys match!");

            foreach (var kvp in potentialDistribution.Potentials)
                PotentialDistribution.Potentials[kvp.Key] = kvp.Value;

            // Set electrode potentials
            foreach(var kvp in potentialDistribution.Potentials)
            {
                var correspondingElectrode = Electrodes.Find(x => x.Id == kvp.Key);

                if(correspondingElectrode != null)
                    correspondingElectrode.Potential = kvp.Value;
            }
        }

        public abstract Mesh DeepCopy();
        public abstract Graph ToGraph();
        public abstract Mesh FromGraph();
    }
}
