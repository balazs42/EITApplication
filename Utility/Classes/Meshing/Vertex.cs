namespace Utility.Classes.Meshing
{
    public class Vertex
    {
        public int Id { get; set; } = -1;
        public double X { get; set; } = 0.0;
        public double Y { get; set; } = 0.0;

        public Vertex(int id)
        {
            Id = id;
            X = 0.0;
            Y = 0.0;
        }

        public Vertex()
        {

        }

        public Vertex(int id, double x, double y)
        {
            Id = id;
            X = x;
            Y = y;
        }
    }
}
