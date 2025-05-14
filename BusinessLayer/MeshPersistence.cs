using TriangleNet.Geometry;
using TriangleNet.Meshing;
using Utility.Classes.Meshing;

using TVertex = TriangleNet.Geometry.Vertex;        // third-party vertex
using TSegment = TriangleNet.Geometry.Segment;
using MVertex = Utility.Classes.Meshing.Vertex;     // your own vertex class

namespace BusinessLayer
{
    public class MeshPersistence : IMeshPersistence
    {
        #region FEMMESH
        // --- FEM Mesh Generation ---
        public FEMMesh GenerateRectangularMesh(double height, double width, int numLayers)
        {
            if (height <= 0 || width <= 0)
                throw new ArgumentOutOfRangeException("Height / width must be positive and non-zero.");

            var boundary = new List<MVertex>
            {
                new MVertex {X = 0,     Y = 0     },
                new MVertex {X = width, Y = 0     },
                new MVertex {X = width, Y = height},
                new MVertex {X = 0,     Y = height}
            };

            return DelaunayTriangulateFEMMesh(boundary, numLayers);
        }

        public FEMMesh GenerateCircularFEMMesh(double radius, int numLayers)
        {
            if (radius <= 0)
                throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be positive.");

            const int N = 64;                          // #points in circular boundary
            double dTheta = 2.0 * Math.PI / N;

            var boundary = new List<MVertex>(N);
            for (int i = 0; i < N; ++i)
            {
                double theta = i * dTheta;
                boundary.Add(new MVertex
                {
                    X = radius * Math.Cos(theta),
                    Y = radius * Math.Sin(theta)
                });
            }

            return DelaunayTriangulateFEMMesh(boundary, numLayers);
        }

        public FEMMesh GeneratePolygonFEMMesh(List<MVertex> polygon, int numLayers)
        {
            return DelaunayTriangulateFEMMesh(polygon, numLayers);  
        }

        #endregion

        #region LBMMESH

        // --- LBM Mesh Generation ---
        public LBMMesh GenerateRectangularLBMMesh(double height, double width)
        {
            throw new NotImplementedException();
        }

        public LBMMesh GenerateCircularLBMMesh(double height, double width, double radius)
        {
            throw new NotImplementedException();
        }

        public LBMMesh GeneratePolygonLBMMesh(double height, double width, List<MVertex> polygon)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region DELAUNAY

        private FEMMesh DelaunayTriangulateFEMMesh(List<MVertex> polygonBoundaryVertices, int numLayers)
        {
            /* --------------- argument checks -------------------------------- */
            if (polygonBoundaryVertices == null || polygonBoundaryVertices.Count < 3)
                throw new ArgumentException("Need at least three boundary vertices.",
                                            nameof(polygonBoundaryVertices));
            if (numLayers < 0)
                throw new ArgumentOutOfRangeException(nameof(numLayers), "Must be ≥ 0.");

            /* --------------- 1. build nested polygon loops ------------------- */
            List<List<MVertex>> loops = BuildPolygonLayers(polygonBoundaryVertices,
                                                           numLayers);

            /* --------------- 2. prepare PSLG for Triangle.NET ---------------- */
            var pslg = new TriangleNet.Geometry.Polygon();

            foreach (List<MVertex> loop in loops)
            {
                int first = pslg.Points.Count;   // index of first vertex in this loop

                /*  vertices --------------------------------------------------- */
                foreach (MVertex v in loop)
                    pslg.Add(new TVertex(v.X, v.Y));

                /*  boundary segments ----------------------------------------- */
                int n = loop.Count;
                for (int i = 0; i < n; ++i)
                {
                    var a = pslg.Points[first + i];
                    var b = pslg.Points[first + (i + 1) % n];
                    pslg.Add(new TSegment(a, b));
                }
            }

            /* --------------- 3. triangulate ---------------------------------- */
            var triMesh = (TriangleNet.Mesh)pslg.Triangulate(
                new ConstraintOptions { ConformingDelaunay = true },
                new QualityOptions()); // defaults are fine for most cases

            /* --------------- 4. convert to your FEM model -------------------- */
            // share vertex instances between elements
            var femVertices = triMesh.Vertices.ToDictionary(
                v => v.ID,
                v => new MVertex { Id = v.ID, X = v.X, Y = v.Y });

            var femElements = new List<FEMElement>();
            int elemId = 0;

            foreach (var tri in triMesh.Triangles)
            {
                femElements.Add(new FEMElement(
                    elemId++,
                    femVertices[tri.GetVertexID(0)],
                    femVertices[tri.GetVertexID(1)],
                    femVertices[tri.GetVertexID(2)]));
            }

            return new FEMMesh(femVertices.Values.ToList(), femElements);
        }

        private static List<List<MVertex>> BuildPolygonLayers(IReadOnlyList<MVertex> outer, int numLayers)
        {
            double cx = outer.Average(v => v.X);
            double cy = outer.Average(v => v.Y);

            var allLoops = new List<List<MVertex>>(numLayers + 1)
            {
                new List<MVertex>(outer)   // outermost loop first
            };

            for (int layer = 1; layer <= numLayers; ++layer)
            {
                double t = (double)layer / (numLayers + 1);   // 0 < t < 1
                var inner = new List<MVertex>(outer.Count);
                foreach (var v in outer)
                {
                    inner.Add(new MVertex
                    {
                        X = v.X + (cx - v.X) * t,
                        Y = v.Y + (cy - v.Y) * t
                    });
                }
                allLoops.Add(inner);
            }
            return allLoops;
        }
        #endregion
    }
}
