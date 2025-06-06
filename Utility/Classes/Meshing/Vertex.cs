namespace Utility.Classes.Meshing
{
    public class Vertex
    {
        // Ids serve as the indexers
        public int GlobalId { get; set; } = -1;
        public int BoundaryId { get; set; } = -1;
        public int ElectrodeId { get; set; } = -1;

        // X,Y is the location of the vertex in the mesh
        public double X { get; set; } = 0.0;
        public double Y { get; set; } = 0.0;

        // Booelans help case checking
        public bool IsBoundary { get; set; } = false;
        public bool IsElectrode { get; set; } = false;

        public Vertex(int globalId)
        {
            GlobalId = globalId;
            X = 0.0;
            Y = 0.0;
        }

        public Vertex()
        {

        }

        public Vertex(int globalId, double x, double y)
        {
            GlobalId = globalId;
            X = x;
            Y = y;
        }

        public Vertex(int globalId, int boundaryId, int electrodeId, double x, double y, bool isBoundary, bool isElectrode)
        {
            GlobalId = globalId;
            BoundaryId = boundaryId;
            ElectrodeId = electrodeId;
            X = x;
            Y = y;
            IsBoundary = isBoundary;
            IsElectrode = isElectrode;
        }
    }
}
