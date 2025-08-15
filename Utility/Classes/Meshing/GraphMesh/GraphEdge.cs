namespace Utility.Classes.Meshing.Graph.Graph
{
    public class GraphEdge
    {
        public GraphVertex[] Vertices { get; set; } = new GraphVertex[2];
        public double Weight { get; set; } = 0.0;

        public GraphEdge(GraphVertex vertex1, GraphVertex vertex2, double weight) 
        {
            Vertices = [vertex1, vertex2];
            Weight = weight;
        }

        public GraphEdge(GraphVertex[] vertices, double weight)
        {
            if (vertices.Length != 2)
                throw new ArgumentOutOfRangeException("Cannot initialize graph edge, you should provide exactly 2 nodes for each edge, check code!");

            Vertices = vertices;
            Weight = weight;
        }
    }
}
