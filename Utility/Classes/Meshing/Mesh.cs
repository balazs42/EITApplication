using System.ComponentModel.DataAnnotations;
using System.Xml.Linq;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.GraphMesh;

namespace Utility.Classes
{
    /// <summary>
    /// Basic interface for a Mesh type later on. This helps the meshes to be a generic type.
    /// </summary>
    public interface IMesh
    {
        void LogMesh();

        ConductivityDistribution GetConductivityDistribution();
        PotentialDistribution GetPotentialDistribution();

        Mesh GetMesh();

        IReadOnlyList<Electrode> GetElectrodes();
        IReadOnlyList<MeshElement> GetElements();
        double[] GetElectrodePotentials();        

        Mesh DeepCopy();
    }

    /// <summary>
    /// This class provides a bridging abstraction between the interface and the actual abstract class
    /// from which mesh structures should inherit from
    /// </summary>
    public abstract class Mesh : IMesh
    {
        public abstract ConductivityDistribution ConductivityDistribution { get; protected set; }
        public abstract PotentialDistribution PotentialDistribution { get; protected set; }

        public ConductivityDistribution GetConductivityDistribution() => ConductivityDistribution;
        public PotentialDistribution GetPotentialDistribution() => PotentialDistribution;
        public Mesh GetMesh() => this;

        public abstract IReadOnlyList<MeshElement> GetElements();
        public abstract IReadOnlyList<Electrode> GetElectrodes();
        public abstract double[] GetElectrodePotentials();

        public abstract void SetConductivityDistribution(ConductivityDistribution cd);
        public abstract void SetPotentialDistribution(PotentialDistribution pd);        

        public abstract void LogMesh();
        public abstract Mesh DeepCopy();

        protected static void ValidateSameKeys(IEnumerable<int> a, IEnumerable<int> b)
        {
            var A = a.OrderBy(x => x).ToArray();
            var B = b.OrderBy(x => x).ToArray();
            if (A.Length != B.Length || !A.SequenceEqual(B))
                throw new ArgumentOutOfRangeException("Key-set mismatch between provided data and mesh state.");
        }
    }

    /// <summary>
    /// All mesh types have to inherit from ths Mesh abstract class. 
    /// This implements basic mesh functionality, like holding electrodes and elements, etc. data.
    /// </summary>
    public abstract class Mesh<TElement, TElectrode> : Mesh
        where TElement : MeshElement
        where TElectrode : Electrode
    {
        protected readonly List<TElement> _elements = [];
        protected readonly List<TElectrode> _electrodes = [];

        public IReadOnlyList<TElement> ElementsTyped => _elements;
        public IReadOnlyList<TElectrode> ElectrodesTyped => _electrodes;

        public sealed override IReadOnlyList<MeshElement> GetElements()
            => _elements.Cast<MeshElement>().ToList();
        public sealed override IReadOnlyList<Electrode> GetElectrodes()
            => _electrodes.Cast<Electrode>().ToList();

        public sealed override double[] GetElectrodePotentials()
            => _electrodes.Select(ReadPotentialOf).ToArray();

        public sealed override void SetConductivityDistribution(ConductivityDistribution cd)
        {
            if (cd is null) throw new ArgumentNullException(nameof(cd));
            ValidateSameKeys(cd.Conductivities.Keys, _elements.Select(e => e.Id));

            ConductivityDistribution = cd;
            foreach (var e in _elements)
                if (cd.Conductivities.TryGetValue(e.Id, out var value))
                    e.Conductivity = value;
        }

        public sealed override void SetPotentialDistribution(PotentialDistribution pd)
        {
            if (pd is null) throw new ArgumentNullException(nameof(pd));
            ValidateSameKeys(pd.Potentials.Keys, StateKeys());

            PotentialDistribution = pd;
            foreach (var kv in pd.Potentials)
                ApplyPotentialToState(kv.Key, kv.Value);

            RefreshElectrodePotentialsFromState();
        }

        public void SetElectrodes(IList<TElectrode> electrodes)
        {
            _electrodes.Clear();

            foreach (var el in electrodes)
                _electrodes.Add(el);
        }

        public void SetElements(IList<TElement> elements)
        {
            if (_elements.Count != elements.Count)
                throw new ArgumentException("Cannot set elements, list count mismatch!");

            _elements.Clear();

            foreach (var el in elements)
                _elements.Add(el);
        }

        public void SetConductivity(int id, double value)
        {
            var el = _elements.Find(x => x.Id == id);

            if (el == null)
                throw new ArgumentOutOfRangeException("Cannot set conductivity, id not found in elements. Check lists!");

            el.Conductivity = value;
            ConductivityDistribution.Conductivities[id] = value;
        }

        protected virtual void RefreshElectrodePotentialsFromState()
        {
            for (int i = 0; i < _electrodes.Count; i++)
                _electrodes[i].Potential = ReadPotentialOf(_electrodes[i]);
        }

        protected abstract IEnumerable<int> StateKeys();                       
        protected abstract void ApplyPotentialToState(int key, double phi); 
        protected abstract double ReadPotentialOf(TElectrode electrode);       
        
        public override ConductivityDistribution ConductivityDistribution { get; protected set; }
        public override PotentialDistribution PotentialDistribution { get; protected set; }

        /// <summary>
        /// Refines the mesh object to a higher resolution one.
        /// </summary>
        /// <param name="n">Number of leves of refinement.</param>
        /// <returns>The refined mesh object.</returns>
        public abstract Mesh<TElement, TElectrode> RefineUniform(int n);
        
        /// <summary>
        /// Convert the spatial discretization of the domain to a graph representation.
        /// </summary>
        /// <returns>A graph object that resembles the original mesh.</returns>
        public abstract Graph ToGraph();

        /// <summary>
        /// Converst a graph object to a spatial discretization of the domain.
        /// </summary>
        /// <param name="graphToConvert">The graph to convert.</param>
        /// <returns>A mesh object that resembles the original graph.</returns>
        public abstract Mesh<TElement, TElectrode> FromGraph(Graph graphToConvert);
    }
}
