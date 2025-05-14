using Utility.Classes.Meshing;

namespace BusinessLayer
{
    public interface IMeshPersistence
    {
        // --- FEM Mesh Generation ---
        public FEMMesh GenerateRectangularMesh(double height, double width, int numLayers);
        public FEMMesh GenerateCircularFEMMesh(double radius, int numLayers);
        public FEMMesh GeneratePolygonFEMMesh(List<Vertex> polygon, int numLayers);

        // --- LBM Mesh Generation ---
        public LBMMesh GenerateRectangularLBMMesh(double height, double width);
        public LBMMesh GenerateCircularLBMMesh(double height, double width, double radius);
        public LBMMesh GeneratePolygonLBMMesh(double height, double width, List<Vertex> polygon);
    }
}
