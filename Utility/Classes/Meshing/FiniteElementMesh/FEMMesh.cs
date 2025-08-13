using Utility.Classes.Factories;

namespace Utility.Classes.Meshing.FiniteElementMesh
{
    public class FEMMesh : Mesh<FEMElement, FEMElectrode>
    {
        public List<Vertex> Vertices { get; set; } = [];

        public FEMMesh(IEnumerable<Vertex> vertices,
                       IEnumerable<FEMElement> elements,
                       IEnumerable<FEMElectrode>? electrodes = null)
        {
            if (vertices != null)
                Vertices.AddRange(vertices);
            if (elements != null)
                _elements.AddRange(elements);
            if (electrodes != null) 
                _electrodes.AddRange(electrodes);

            Initialize();
        }

        public FEMMesh()
        {
            Initialize();
        }

        public List<Vertex> GetVertice() => Vertices;

        public void Initialize()
        {
            // Initialize with a homogeneous conductivity distribution
            ConductivityDistribution = ConductivityDistributionFactory.FromFEMMesh(this);

            Dictionary<int, double> potentialDistribution = new Dictionary<int, double>();

            foreach (var vertex in Vertices)
                potentialDistribution.Add(vertex.GlobalId, vertex.Potential);

            PotentialDistribution = new PotentialDistribution(potentialDistribution);
        }

        protected override IEnumerable<int> StateKeys() => Vertices.Select(v => v.GlobalId);

        protected override void ApplyPotentialToState(int stateKey, double potential)
        {
            var v = Vertices.FirstOrDefault(x => x.GlobalId == stateKey)
                    ?? throw new InvalidOperationException($"No Vertex.GlobalId = {stateKey}.");
            v.Potential = potential;
        }

        protected override double ReadPotentialOf(FEMElectrode e)
        {
            if (!e.PointElectrode && e.VertexIds != null && e.VertexIds.Count > 0)
            {
                return e.VertexIds
                        .Select(id => Vertices.FirstOrDefault(v => v.GlobalId == id)
                                      ?? throw new InvalidOperationException($"No Vertex.GlobalId = {id}."))
                        .Select(v => v.Potential)
                        .Average();
            }

            var vv = Vertices.FirstOrDefault(v => v.GlobalId == e.MeshId)
                     ?? throw new InvalidOperationException($"No Vertex.GlobalId = {e.MeshId} (FEMElectrode.MeshId).");
            return vv.Potential;
        }


        /// <summary>
        /// Creates a deep copy of this FEMMesh, including vertices, elements,
        /// electrode list, and distributions.
        /// </summary>
        public override Mesh DeepCopy()
        {
            var vertexMap = new Dictionary<int, Vertex>(Vertices.Count);
            var newVertices = new List<Vertex>(Vertices.Count);

            foreach (var v in Vertices)
            {
                var v2 = new Vertex
                {
                    GlobalId = v.GlobalId,
                    BoundaryId = v.BoundaryId,
                    ElectrodeId = v.ElectrodeId,
                    X = v.X,
                    Y = v.Y,
                    IsBoundary = v.IsBoundary,
                    IsElectrode = v.IsElectrode,
                    Potential = v.Potential
                };
                vertexMap[v.GlobalId] = v2;
                newVertices.Add(v2);
            }

            var newElements = new List<FEMElement>(_elements.Count);
            foreach (var el in _elements)
            {
                var a = vertexMap[el.Vertices[0].GlobalId];
                var b = vertexMap[el.Vertices[1].GlobalId];
                var c = vertexMap[el.Vertices[2].GlobalId];

                var el2 = new FEMElement(el.Id, a, b, c)
                {
                    Conductivity = el.Conductivity
                };
                newElements.Add(el2);
            }

            var copy = new FEMMesh(newVertices, newElements);

            if (_electrodes.Count > 0)
            {
                foreach (var e in _electrodes)
                {
                    var e2 = new FEMElectrode(
                        id: e.Id,
                        meshId: e.MeshId,
                        current: e.Current,
                        zContact: e.ZContact,
                        voltage: e.Potential,
                        isExcitation: e.IsExcitation,
                        isGround: e.IsGround,
                        isMeasuring: e.IsMeasuring,
                        pointElectrode: e.PointElectrode
                    );
                    if (e.VertexIds?.Count > 0)
                        e2.VertexIds.AddRange(e.VertexIds);

                    copy.ElectrodesTyped.ToList().Add(e2); 
                }
            }

            copy.SetElectrodes(_electrodes);
            copy.SetConductivityDistribution(new ConductivityDistribution(this.ConductivityDistribution.Conductivities));
            copy.SetPotentialDistribution(new PotentialDistribution(this.PotentialDistribution.Potentials));

            return copy;
        }

        public override void LogMesh()
        {
            Console.WriteLine($"FEM | V={Vertices.Count}, E={_elements.Count}, EL={_electrodes.Count}");
        }

        public override GraphMesh.Graph ToGraph()
        {
            throw new NotImplementedException();
        }

        public override Mesh FromGraph()
        {
            throw new NotImplementedException();
        }
    }
}
