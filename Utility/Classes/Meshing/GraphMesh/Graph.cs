using Utility.Classes.Meshing.Graph.Graph;

namespace Utility.Classes.Meshing.GraphMesh
{
    public sealed class Graph
    {
        public List<GraphFEMVertex> Vertices { get; set; } = [];
        public List<GraphEdge> Edges { get; set; } = [];
        public int NodeCount { get; set; } = -1;
        public int EdgeCount { get; set; } = -1;

        /// <summary>
        /// Initializes the graph with the provided list.
        /// </summary>
        /// <param name="vertices">The list of vertices properly labeled with domain and boundary ids.</param>
        /// <param name="edges">The edges connecting the graph vertices.</param>
        public Graph(List<GraphFEMVertex> vertices, List<GraphEdge> edges)
        {
            Vertices = vertices;
            Edges = edges;
            NodeCount = vertices.Count;
            EdgeCount = edges.Count;
        }
    }
}
