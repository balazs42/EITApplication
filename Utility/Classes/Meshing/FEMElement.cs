namespace Utility.Classes.Meshing
{
    public sealed class FEMElement : MeshElement
    {
        public double Area { get; set; }

        public new List<Vertex> Vertices { get; set; } = [new Vertex(), new Vertex(), new Vertex()];

        public FEMElement(int id, Vertex v1, Vertex v2, Vertex v3) 
        {
            Id = id;
            Vertices = [v1, v2, v3];

            CalculateArea();
        }

        private void CalculateArea()
        {
            if(Vertices.Count == 0)
                throw new ArgumentNullException(nameof(Vertices));

            Vertex V1 = Vertices[0];
            Vertex V2 = Vertices[1];
            Vertex V3 = Vertices[2];


            Area = Math.Abs(0.5 *
                            (V1.X * (V2.Y - V3.Y) +
                             V2.X * (V3.Y - V1.Y) +
                             V3.X * (V1.Y - V2.Y)));
        }
    }
}
