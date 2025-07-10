using Utility.Classes.Factories;

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

            Initialize();
        }

        public FEMMesh()
        {
            Initialize();
        }

        public void Initialize()
        {
            // Initialize with a homogeneous conductivity distribution
            this.ConductivityDistribution = ConductivityDistributionFactory.FromFEMMesh(this);

            Dictionary<int, double> potentialDistribution = new Dictionary<int, double>();
            foreach (var vertex in Vertices)
                potentialDistribution.Add(vertex.GlobalId, vertex.Potential);
            PotentialDistribution = new PotentialDistribution(potentialDistribution);
        }

        public new ConductivityDistribution GetConductivityDistribution()
        {
            Dictionary<int, double> cd = new Dictionary<int, double>();

            foreach(var element in  Elements)
                cd.Add(element.Id, element.Conductivity);

            ConductivityDistribution = new ConductivityDistribution(cd);

            return ConductivityDistribution;
        }


        public override double[] GetElectrodePotentials()
        {
            // First update the electrode potentials, if they were not updated
            PotentialDistribution potentialDistribution = PotentialDistribution;

            for(int i = 0; i < Electrodes.Count; i++)
            {
                foreach (var kvp in potentialDistribution.Potentials)
                {
                    if (kvp.Key == Electrodes[i].MeshId)
                    {
                        Electrodes[i].Voltage = kvp.Value;
                        break;
                    }
                }
            }

            double[] potentials = new double[Electrodes.Count];

            for (int i = 0; i < potentials.Length; i++)
                potentials[i] = Electrodes[i].Voltage;

            return potentials;
        }

        // Set the conductivity distribution of the mesh, and also sets each elements conductivity
        // according to the provided distribution
        public void SetConductivityDistribution(ConductivityDistribution conductivityDistribution)
        {
            if (conductivityDistribution.Conductivities.Count != ConductivityDistribution.Conductivities.Count)
                throw new ArgumentOutOfRangeException("Cannot set conductivity distribution on mesh, since the provided distribution contains differing number of elements, then the mesh. Check code!");

            ConductivityDistribution = conductivityDistribution;

            foreach(var kvp in ConductivityDistribution.Conductivities)
            {
                foreach(var element in Elements)
                {
                    if(element.Id == kvp.Key)
                    {
                        element.Conductivity = kvp.Value;
                        break;
                    }
                }
            }
        }
        
        public void SetPotentialDistribution(PotentialDistribution potentialDistribution)
        {
            if(potentialDistribution.Potentials.Count != PotentialDistribution.Potentials.Count)
                throw new ArgumentOutOfRangeException("Cannot set potential distribution on mesh, since the provided distribution contains differing number of elements, then the mesh. Check code!");

            PotentialDistribution = potentialDistribution;

            foreach(var kvp in PotentialDistribution.Potentials)
            {
                foreach(var vertex in Vertices)
                {
                    if(vertex.GlobalId == kvp.Key)
                    {
                        vertex.Potential = kvp.Value;
                        break;
                    }
                }
            }

            // Set electrode potentials as well
            double[] electrodePotentials = new double[Electrodes.Count];

            foreach (var vertex in Vertices)
                if (vertex.IsElectrode)
                    electrodePotentials[vertex.ElectrodeId] = vertex.Potential;

            SetElectrodePotentials(electrodePotentials);
        }


        public void SetElectrodePotentials(List<double> potentials)
        {
            if (potentials.Count != Electrodes.Count)
                throw new ArgumentOutOfRangeException("Cannot set electrode potentials, if list size mistamtch electrodes list count, check code!");

            for (int i = 0; i < potentials.Count; i++)
                Electrodes[i].Voltage = potentials[i];
        }

        public void SetElectrodePotentials(double[] potentials)
        {
            if (potentials.Length != Electrodes.Count)
                throw new ArgumentOutOfRangeException("Cannot set electrode potentials, if list size mistamtch electrodes list count, check code!");

            for (int i = 0; i < potentials.Length; i++)
                Electrodes[i].Voltage = potentials[i];
        }

        /// <summary>
        /// Creates a deep copy of this FEMMesh, including vertices, elements,
        /// electrode list, and distributions.
        /// </summary>
        public FEMMesh DeepCopy()
        {
            // 1) Clone vertices
            var vertexMap = new Dictionary<int, Vertex>();
            var newVertices = new List<Vertex>(Vertices.Count);
            foreach (var v in Vertices)
            {
                var v2 = new Vertex(v.GlobalId, v.X, v.Y)
                {
                    Potential = v.Potential,
                    IsBoundary = v.IsBoundary,
                    BoundaryId = v.BoundaryId,
                    IsElectrode = v.IsElectrode,
                    ElectrodeId = v.ElectrodeId
                };
                vertexMap[v.GlobalId] = v2;
                newVertices.Add(v2);
            }

            // 2) Clone elements referencing new vertices
            var newElements = new List<FEMElement>(Elements.Count);
            foreach (var el in Elements)
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

            // 3) Construct new mesh
            var copy = new FEMMesh(newVertices, newElements);

            // 4) Clone electrodes
            copy.Electrodes = new List<Electrode>(Electrodes.Count);
            foreach (var el in Electrodes)
            {
                var el2 = new Electrode(el.Id, el.MeshId, el.Current, el.ZContact, el.Voltage)
                {
                    IsGround = el.IsGround,
                    IsExcitation = el.IsExcitation
                };
                el2.VertexIds.AddRange(el.VertexIds);
                copy.Electrodes.Add(el2);
            }

            // 5) Clone distributions
            copy.ConductivityDistribution = new ConductivityDistribution(
                new Dictionary<int, double>(this.ConductivityDistribution.Conductivities));
            copy.PotentialDistribution = new PotentialDistribution(
                new Dictionary<int, double>(this.PotentialDistribution.Potentials));

            return copy;
        }
    }
}
