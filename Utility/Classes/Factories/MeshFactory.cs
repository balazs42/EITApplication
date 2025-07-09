using Utility.Classes.Meshing;
using MIConvexHull;                           // ← Delaunay triangulation

namespace Utility.Classes.Factories
{

    public static class MeshFactory
    {
        public static IMesh Create(MeshType mt, int layers = 2, int boundaryVertexCount = 16) => mt switch
        {
            MeshType.FEM => CreateCircularFEMMesh(layers: layers,
                                                  boundaryVertexCount: boundaryVertexCount,
                                                  inhomogeneityValue: 3.0),
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
            return CreateCircularFEMMeshInternal(layers, boundaryVertexCount, electrodeCount, inhomogeneityValue: 1.0);
        }

        /// <summary>
        /// Builds an inhomogeneous circular FEM mesh where elements in the inner rings
        /// have conductivity scaled by inhomogeneityValue (default 3.0).
        /// </summary>
        public static FEMMesh CreateCircularFEMMesh(
            int layers,
            int boundaryVertexCount,
            double inhomogeneityValue = 3.0,
            int electrodeCount = 16)
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
                    vertices.Add(new Vertex(vid++,
                        rnorm * Math.Cos(theta),
                        rnorm * Math.Sin(theta))
                    {
                        IsBoundary = (layer == layers),
                        BoundaryId = (layer == layers ? i : -1)
                    });
                }
            }

            // 2) triangulate
            var triVerts = vertices.Select(v => new TriVertex(v)).ToArray();
            var delaunay = DelaunayTriangulation<TriVertex, DefaultTriangulationCell<TriVertex>>
                                .Create(triVerts, 1e-3);

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
                {
                    elem.Conductivity *= inhomogeneityValue;
                }
                elements.Add(elem);
            }

            // 4) assemble mesh
            var mesh = new FEMMesh(vertices, elements);

            // 5) distribute electrodes
            var outerVerts = vertices.Where(v => v.IsBoundary).ToList();
            double sector = 2 * Math.PI / electrodeCount;
            var electrodes = new List<Electrode>(electrodeCount);
            for (int elId = 0; elId < electrodeCount; elId++)
            {
                double start = elId * sector;
                double end = start + sector;
                var segVerts = outerVerts.Where(v =>
                {
                    double ang = Math.Atan2(v.Y, v.X);
                    if (ang < 0) ang += 2 * Math.PI;
                    return ang >= start && ang < end;
                }).ToList();
                foreach (var v in segVerts)
                {
                    v.IsElectrode = true;
                    v.ElectrodeId = elId;
                }
                var el = new Electrode(elId, elId, 0.0, 0.1, 1.0);
                el.VertexIds.AddRange(segVerts.Select(v => v.GlobalId));
                electrodes.Add(el);
            }
            mesh.Electrodes = electrodes;
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
