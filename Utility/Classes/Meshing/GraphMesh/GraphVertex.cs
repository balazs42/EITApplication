namespace Utility.Classes.Meshing.Graph.Graph
{
    /// <summary>
    /// Represents a vertex in a standard graph structure, can be used for planar resistor network reconstruciton.
    /// </summary>
    public class GraphVertex
    {
        public double X { get; set; }
        public double Y { get; set; }
        public int GlobalId { get; set; }
        public int DomainId { get; set; }
        public int BoundaryId { get; set; }
        public double Potential { get; set; } = 0.0;

        public GraphVertex(double x, double y, int globalId, int domainId, int boundaryId)
        {
            X = x;
            Y = y;
            GlobalId = globalId;
            DomainId = domainId;
            BoundaryId = boundaryId;
        }
    }
}
