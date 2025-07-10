using Utility.Classes.Meshing;
using MIConvexHull;

namespace Utility.Classes.Factories
{

    public static class MeshFactory
    {
        public static IMesh Create(MeshType mt, int layers = 2, int boundaryVertexCount = 16, int electrodeCount = 16, double inhomogenityValue = 10.0) => mt switch
        {
            MeshType.FEM => CreateCircularFEMMesh(layers: layers,
                                                  boundaryVertexCount: boundaryVertexCount,
                                                  electrodeCount: electrodeCount,
                                                  inhomogeneityValue: inhomogenityValue),
            MeshType.LBM => new LBMMesh(),
            _ => throw new NotSupportedException()
        };


        /// <summary>
        /// Builds a circular FEM mesh with given concentric layers and boundary vertices,
        /// then distributes `electrodeCount` electrodes evenly around the outer boundary.
        /// </summary>
        public static FEMMesh CreateCircularFEMMesh(
            int layers,
            int boundaryVertexCount,
            int electrodeCount = 16)
        {
            return CreateCircularFEMMeshInternal(layers, boundaryVertexCount, electrodeCount, inhomogeneityValue: 3.0);
        }

        /// <summary>
        /// Builds an inhomogeneous circular FEM mesh where elements in the inner rings
        /// have conductivity scaled by inhomogeneityValue (default 3.0).
        /// </summary>
        public static FEMMesh CreateCircularFEMMesh(
            int layers,
            int boundaryVertexCount,
            int electrodeCount = 16,
            double inhomogeneityValue = 3.0)
        {
            return CreateCircularFEMMeshInternal(layers, boundaryVertexCount, electrodeCount, inhomogeneityValue);
        }

        // common implementation with inhomogeneity scaling
        private static FEMMesh CreateCircularFEMMeshInternal(
            int layers,
            int boundaryVertexCount,
            int electrodeCount,
            double inhomogeneityValue)
        {
            if (electrodeCount > boundaryVertexCount)
                electrodeCount = boundaryVertexCount;

            // 1) build vertices (center + rings)
            var vertices = new List<Vertex>();
            int vid = 0;

            // center
            vertices.Add(new Vertex(vid++, 0, 0) { IsBoundary = (layers == 0) });

            // concentric rings
            for (int layer = 1; layer <= layers; layer++)
            {
                double rnorm = (double)layer / layers;
                for (int i = 0; i < boundaryVertexCount; i++)
                {
                    double theta = 2 * Math.PI * i / boundaryVertexCount;
                    vertices.Add(new Vertex(globalId: vid++,
                                            x: rnorm * Math.Cos(theta),
                                            y: rnorm * Math.Sin(theta))
                                 {
                                     IsBoundary = (layer == layers),
                                     BoundaryId = (layer == layers ? i : -1)
                                 });
                }
            }

            // 2) triangulate
            var triVerts = vertices.Select(v => new TriVertex(v)).ToArray();
            var delaunay = DelaunayTriangulation<TriVertex, DefaultTriangulationCell<TriVertex>>.Create(triVerts, 1e-3);

            // 3) create elements with inhomogeneous conductivity
            var elements = new List<FEMElement>();
            int eid = 0;
            double innerRadius = 1.0 / (layers + 1e-4); // threshold

            foreach (var cell in delaunay.Cells)
            {
                var a = cell.Vertices[0].Original;
                var b = cell.Vertices[1].Original;
                var c = cell.Vertices[2].Original;

                var elem = new FEMElement(eid++, a, b, c);

                // compute average radial distance of element vertices
                double rA = Math.Sqrt(a.X * a.X + a.Y * a.Y);
                double rB = Math.Sqrt(b.X * b.X + b.Y * b.Y);
                double rC = Math.Sqrt(c.X * c.X + c.Y * c.Y);

                double avgRadius = (rA + rB + rC) / 3.0;

                // if inside inner circle, scale conductivity
                if (avgRadius < innerRadius)
                    elem.Conductivity = inhomogeneityValue;
                else
                    elem.Conductivity = 1.0;

                elements.Add(elem);
            }

            // 4) assemble mesh
            var mesh = new FEMMesh(vertices, elements);

            // 5) distribute electrodes

            // clear any leftover flags
            foreach (var v in vertices)
            {
                v.IsElectrode = false;
                v.ElectrodeId = -1;
            }

            // gather boundary vertices sorted by their BoundaryId
            var boundaryVerts = vertices
                .Where(v => v.IsBoundary)
                .OrderBy(v => v.BoundaryId)
                .ToList();
            int boundaryCount = boundaryVerts.Count;
            int increment = Math.Max(boundaryCount / electrodeCount, 1);

            var electrodes = new List<Electrode>(electrodeCount);
            for (int elId = 0; elId < electrodeCount; elId++)
            {
                // pick every 'increment' boundary vertex
                int idx = elId * increment;
                if (idx >= boundaryCount) idx = boundaryCount - 1;

                var v = boundaryVerts[idx];
                v.IsElectrode = true;
                v.ElectrodeId = elId;

                var el = new Electrode(
                    id: elId,
                    meshId: v.GlobalId,
                    current: 0.0,
                    zContact: 0.1,
                    voltage: 1.0);

                el.VertexIds.Add(v.GlobalId);
                electrodes.Add(el);
            }

            mesh.Electrodes = electrodes;

            // Assing vertex neighbors
            foreach (var element in elements)
            {
                Vertex V1 = element.Vertices[0];
                Vertex V2 = element.Vertices[1];
                Vertex V3 = element.Vertices[2];

                //  ---------------
                //  |             |
                // v1 --- v2 --- v3
                V1.Neighbors.Add(V2); V1.Neighbors.Add(V3);
                V2.Neighbors.Add(V1); V2.Neighbors.Add(V3);
                V3.Neighbors.Add(V1); V3.Neighbors.Add(V2);
            }

            // Remove duplicates
            foreach(var element in elements)
            {
                for(int i = 0; i < 3; i++)
                {
                    // Find any duplicates
                    Vertex v = element.Vertices[i];

                    for(int j = 0; j < v.Neighbors.Count; j++)
                    {
                        List<Vertex> neighbors = v.Neighbors;
                        for(int k = 0; k < v.Neighbors.Count; k++)
                        {
                            if ((neighbors[k].GlobalId == neighbors[j].GlobalId) && k != j)
                            {
                                neighbors.RemoveAt(k);
                                j = 0;
                                k = 0;
                                break;
                            }
                        }
                    }
                }
            }

            // Initialize should be called, because that is what updates the mesh conductivity distribution and potential distribution
            mesh.Initialize();
            return mesh;
        }

        private class TriVertex : IVertex
        {
            public double[] Position { get; }
            public Vertex Original { get; }

            public TriVertex(Vertex v)
            {
                Original = v;
                Position = new[] { v.X, v.Y };
            }
        }
    }
}
