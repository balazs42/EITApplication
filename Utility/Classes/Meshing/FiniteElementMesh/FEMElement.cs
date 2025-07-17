using System.Diagnostics.CodeAnalysis;

namespace Utility.Classes.Meshing.FiniteElementMesh
{
    public sealed class FEMElement : MeshElement
    {
        public double Area { get; set; }
        public List<Vertex> Vertices { get; set; } = [new Vertex(), new Vertex(), new Vertex()];
        public double[,] GradPhi { get; private set; } = new double[3, 3]; // Gradients of shape functions
        public double[,] DotProducts { get; private set; } = new double[3, 3];

        public FEMElement(int id, Vertex v1, Vertex v2, Vertex v3) 
        {
            Id = id;
            Vertices = [v1, v2, v3];

            Initialize();
        }

        public FEMElement(int id, Vertex v1, Vertex v2, Vertex v3, double conductivity)
        {
            Id = id;
            Vertices = [v1, v2, v3];
            Conductivity = conductivity;

            Initialize();
        }

        private void Initialize()
        {
            CalculateArea();

            // Calculate shape function gradients on the given element
            CalculateGradients();

            // Calculate the dot product of the gradients of shape functions on given element
            CalculateDotProducts();

            if (GradPhi == null || DotProducts == null)
                throw new InvalidDataException("GradPhi or DotProducts was null during element initialization, check code!");        
        }

        private void CalculateArea()
        {
            if(Vertices.Count == 0)
                throw new ArgumentNullException(nameof(Vertices));

            Vertex V1 = Vertices[0];
            Vertex V2 = Vertices[1];
            Vertex V3 = Vertices[2];

            Area = 0.5 *  Math.Abs(V1.X * (V2.Y - V3.Y) +
                                    V2.X * (V3.Y - V1.Y) +
                                    V3.X * (V1.Y - V2.Y));
        }

        // Calculate gradients of spahe functions beforehand
        private void CalculateGradients()
        {
            Vertex V1 = Vertices[0];
            Vertex V2 = Vertices[1];
            Vertex V3 = Vertices[2];

            // Gradients of the linear shape functions are constant within the element
            double x1 = V1.X, y1 = V1.Y;
            double x2 = V2.X, y2 = V2.Y;
            double x3 = V3.X, y3 = V3.Y;

            GradPhi = new double[3, 2];

            // ∇ϕ₁
            GradPhi[0, 0] = (y2 - y3) / (2.0 * Area);  // d/dx
            GradPhi[0, 1] = (x3 - x2) / (2.0 * Area);  // d/dy

            // ∇ϕ₂
            GradPhi[1, 0] = (y3 - y1) / (2.0 * Area);
            GradPhi[1, 1] = (x1 - x3) / (2.0 * Area);

            // ∇ϕ₃
            GradPhi[2, 0] = (y1 - y2) / (2.0 * Area);
            GradPhi[2, 1] = (x2 - x1) / (2.0 * Area);
        }

        // Calculate the dot product of gradients of shape functions
        private void CalculateDotProducts()
        {
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double dotPorduct = GradPhi[i, 0] * GradPhi[j, 0] +
                                        GradPhi[i, 1] * GradPhi[j, 1];

                    DotProducts[i, j] = dotPorduct;
                }
            }
        }
    }
}
