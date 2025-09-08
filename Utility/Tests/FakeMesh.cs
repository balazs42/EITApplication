using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;

namespace Utility.Tests
{
    /// <summary>Minimal mesh double. Enough for units that don’t actually need FEM/LBM.</summary>
    internal sealed class FakeMesh : IDiscretization
    {
        private sealed class FakeElement : DiscretizationElement { }
        private sealed class FakeElectrode : Electrode { }

        private readonly List<Electrode> _electrodes = new();
        private readonly List<FEMVertex> _vertices = new();
        private readonly List<DiscretizationElement> _elements = new();

        private ConductivityDistribution _sigma = new(new Dictionary<int, double>());
        private PotentialDistribution _phi = new(new Dictionary<int, double>());

        public DiscretizationMetaData Metadata { get; set; } = new();

        public FakeMesh(int nElems = 0, int nElectrodes = 0)
        {
            for (int i = 0; i < nElems; i++)
            {
                var el = new FakeElement { Id = i, Conductivity = 1.0 };
                _elements.Add(el);
                _sigma.Conductivities[i] = el.Conductivity;
            }

            for (int i = 0; i < nElectrodes; i++)
            {
                var v = new FEMVertex(i, 0.0, 0.0) { IsElectrode = true, ElectrodeId = i };
                _vertices.Add(v);
                var el = new FakeElectrode { Id = i, Potential = 0.0 };
                _electrodes.Add(el);
            }
        }

        public void LogDiscretization() { }
        public ConductivityDistribution GetConductivityDistribution() => _sigma;
        public PotentialDistribution GetPotentialDistribution() => _phi;
        public Discretization GetDiscretization() => null!; // not used in these tests

        public IReadOnlyList<Electrode> GetElectrodes() => _electrodes;
        public IReadOnlyList<FEMVertex> GetVertices() => _vertices;
        public IReadOnlyList<DiscretizationElement> GetElements() => _elements;

        public double[] GetElectrodePotentials() => _electrodes.Select(e => e.Potential).ToArray();
        public IReadOnlyList<FEMVertex> GetElectrodeVertices() => _vertices.Where(v => v.IsElectrode).ToList();

        public Discretization DeepCopy() => null!;
        public Classes.Discretizer.GraphMesh.Graph ToGraph() => null!;
        public Discretization FromGraph() => null!;

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
