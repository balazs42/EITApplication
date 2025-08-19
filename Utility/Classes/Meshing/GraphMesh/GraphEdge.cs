namespace Utility.Classes.Meshing.Graph.Graph
{
    public class GraphEdge
    {
        public GraphFEMVertex[] Vertices { get; set; } = new GraphFEMVertex[2];
        public double Weight { get; set; } = 0.0;

        public GraphEdge(GraphFEMVertex FEMVertex1, GraphFEMVertex FEMVertex2, double weight) 
        {
            Vertices = [FEMVertex1, FEMVertex2];
            Weight = weight;
        }

        public GraphEdge(GraphFEMVertex[] vertices, double weight)
        {
            if (vertices.Length != 2)
                throw new ArgumentOutOfRangeException("Cannot initialize graph edge, you should provide exactly 2 nodes for each edge, check code!");

            Vertices = vertices;
            Weight = weight;
        }
    }
}
