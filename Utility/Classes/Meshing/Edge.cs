namespace Utility.Classes.Meshing
{
    public class Edge
    {
        public Vertex Start { get; set; } = new(0, 0.0, 0.0);
        public Vertex End { get; set; } = new(0, 0.0, 0.0);

        public Edge(Vertex start, Vertex end)
        {
            Start = start;
            End = end;
        }
    }
}
