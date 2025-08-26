using Utility.Classes;
using Utility.Classes.Meshing;

namespace Utility.Tests
{
    /// <summary>Minimal mesh double. Enough for units that don’t actually need FEM/LBM.</summary>
    internal sealed class FakeMesh : IMesh
    {
        private readonly List<Electrode> _electrodes = new();
        private readonly List<FEMVertex> _vertices = new();
        private readonly List<MeshElement> _elements = new();

        private ConductivityDistribution _sigma = new(new Dictionary<int, double>());
        private PotentialDistribution _phi = new(new Dictionary<int, double>());

        public MeshMetadata Metadata { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public FakeMesh(int nElems = 0, int nElectrodes = 0)
        {
            // TODO: Implement correct fake maesh
            //for (int i = 0; i < nElems; i++) _elements.Add(new MeshElement { Id = i, Conductivity = 0.0 });
            //for (int i = 0; i < nElectrodes; i++) _electrodes.Add(new Electrode { Id = i, Potential = 0.0 });
        }

        public void LogMesh() { }
        public ConductivityDistribution GetConductivityDistribution() => _sigma;
        public PotentialDistribution GetPotentialDistribution() => _phi;
        public Mesh GetMesh() => null!; // not used in these tests

        public IReadOnlyList<Electrode> GetElectrodes() => _electrodes;
        public IReadOnlyList<FEMVertex> GetVertices() => _vertices;
        public IReadOnlyList<MeshElement> GetElements() => _elements;

        public double[] GetElectrodePotentials() => _electrodes.Select(e => e.Potential).ToArray();
        public IReadOnlyList<FEMVertex> GetElectrodeVertices() => _vertices.Where(v => v.IsElectrode).ToList();

        public Mesh DeepCopy() => null!;
        public Classes.Meshing.GraphMesh.Graph ToGraph() => null!;
        public Mesh FromGraph() => null!;

        public void SetConductivityDistribution(ConductivityDistribution cd) => _sigma = cd;
        public void SetPotentialDistribution(PotentialDistribution pd) => _phi = pd;
    }

    internal static class TestData
    {
        public static ConductivityDistribution Sigma(params (int id, double val)[] kv)
            => new(kv.ToDictionary(x => x.id, x => x.val));

        public static ConductivityDistribution Grad(params (int id, double g)[] kv)
            => new(kv.ToDictionary(x => x.id, x => x.g));

        public static double[,] Mat(params double[] data)
        {
            // Creates a 2D matrix inferred as NxN or rectangular (r x c where r*c = len).
            throw new NotImplementedException("Use inline array[,] literals in tests for clarity.");
        }
    }
}
