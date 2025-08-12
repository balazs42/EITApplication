using Utility.Classes.Factories;

namespace Utility.Classes.Meshing.FiniteElementMesh
{
    public class FEMMesh : Mesh
    {
        public new List<FEMElement> Elements { get; set; } = [];
        public new List<FEMElectrode> Electrodes { get; set; } = [];

        public FEMMesh(List<Vertex> vertices, List<FEMElement> elements)
        {
            Vertices = vertices;

            // It's important to set both the specific and the base collection
            // so that methods on the base Mesh class work correctly.
            Elements = elements;
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
            ConductivityDistribution = ConductivityDistributionFactory.FromFEMMesh(this);

            Dictionary<int, double> potentialDistribution = new Dictionary<int, double>();
            foreach (var vertex in Vertices)
                potentialDistribution.Add(vertex.GlobalId, vertex.Potential);
            PotentialDistribution = new PotentialDistribution(potentialDistribution);
        }

        /// <summary>
        /// Returns the conductivity distribution object of the mesh.
        /// </summary>
        /// <returns>ConductivityDistribution of the mesh.</returns>
        public new ConductivityDistribution GetConductivityDistribution()
        {
            Dictionary<int, double> cd = new Dictionary<int, double>();

            foreach(var element in  Elements)
                cd.Add(element.Id, element.Conductivity);

            ConductivityDistribution = new ConductivityDistribution(cd);

            return ConductivityDistribution;
        }

        /// <summary>
        /// Returns the electrode potentials in an ordered array.
        /// </summary>
        /// <returns>The ordered array of electrode potentials.</returns>
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
                        Electrodes[i].Potential = kvp.Value;
                        break;
                    }
                }
            }

            base.Electrodes = Electrodes.Cast<Electrode>().ToList();

            double[] potentials = new double[Electrodes.Count];

            for (int i = 0; i < potentials.Length; i++)
                potentials[i] = Electrodes[i].Potential;

            return potentials;
        }


        /// <summary>
        /// Set the conductivity distribution of the mesh, and also sets each elements conductivity
        /// according to the provided distribution.
        /// </summary>
        /// <param name="conductivityDistribution">The conductivity distribution to set the mesh.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throws if conductivity distribution counts are incomatible.</exception>
        public new void SetConductivityDistribution(ConductivityDistribution conductivityDistribution)
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

            base.Elements = Elements.Cast<MeshElement>().ToList();
        }

        /// <summary>
        /// Sets the potential distrubution of the mesh, by setting the maching verted ids to the provided distribution  
        /// also sets the electrode potentials, after setting the vertex potentials.
        /// </summary>
        /// <param name="potentialDistribution">The provided potential distribution</param>
        /// <exception cref="ArgumentOutOfRangeException">If the provided distribution does not contain same number of nodes, throws.</exception>
        public new void SetPotentialDistribution(PotentialDistribution potentialDistribution)
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

        /// <summary>
        /// Sets the electrode potentials of the mesh to the provided list. The provided list should contain the electrode potentials ordered as 1st to last electrode.
        /// </summary>
        /// <param name="potentials">List of potentials that will be set for the electrodes.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throws if list sizes are not compatible.</exception>
        public void SetElectrodePotentials(IList<double> potentials)
        {
            if (potentials.Count != Electrodes.Count)
                throw new ArgumentOutOfRangeException("Cannot set electrode potentials, if list size mistamtch electrodes list count, check code!");

            for (int i = 0; i < potentials.Count; i++)
                Electrodes[i].Potential = potentials[i];

            base.Electrodes = Electrodes.Cast<Electrode>().ToList();
        }

        /// <summary>
        /// Creates a deep copy of this FEMMesh, including vertices, elements,
        /// electrode list, and distributions.
        /// </summary>
        public override FEMMesh DeepCopy()
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
            copy.Electrodes = new List<FEMElectrode>(Electrodes.Count);
            foreach (var el in Electrodes)
            {
                var el2 = new FEMElectrode(el.Id, el.MeshId, el.Current, el.ZContact, el.Potential)
                {
                    IsGround = el.IsGround,
                    IsExcitation = el.IsExcitation
                };
                el2.VertexIds.AddRange(el.VertexIds);
                copy.Electrodes.Add(el2);
            }

            // 5) Clone distributions
            copy.ConductivityDistribution = new ConductivityDistribution(
                new Dictionary<int, double>(ConductivityDistribution.Conductivities));
            copy.PotentialDistribution = new PotentialDistribution(
                new Dictionary<int, double>(PotentialDistribution.Potentials));

            return copy;
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
