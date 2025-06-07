namespace Utility.Classes.Meshing
{
    public sealed class LBMElement : MeshElement
    {
        public new List<Vertex> Vertices = [new Vertex(), new Vertex(), new Vertex(), new Vertex()];
        public double[] Fi = new double[9];
        public bool IsWall = false;
        public bool IsPinned = false;
        public double Conductivity = 0.0;

        public LBMElement(List<Vertex> vertices, bool isWall)
        {
            Vertices = vertices;
            IsWall = isWall;
        }

        public LBMElement(double x1, double y1, int id1,
                          double x2, double y2, int id2,
                          double x3, double y3, int id3, 
                          double x4, double y4, int id4,
                          bool isWall)
        {
            Vertices = [new Vertex(id1, x1, y1), new Vertex(id2, x2, y2), new Vertex(id3, x3, y3), new Vertex(id4, x4, y4)];
            IsWall = isWall;
        }

        public LBMElement(Vertex v1, Vertex v2, Vertex v3, Vertex v4, bool isWall)
        {
            Vertices = [v1, v2, v3, v4];
            IsWall = isWall;
        }

        public LBMElement(List<Vertex> vertices, double[] fi, bool isWall, bool isPinned, double conductivity)
        {
            Vertices = vertices;
            this.Fi = fi;
            IsWall = isWall;
            IsPinned = isPinned;
            Conductivity = conductivity;
        }
    }
}
