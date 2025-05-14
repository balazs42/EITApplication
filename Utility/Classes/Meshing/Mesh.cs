namespace Utility.Classes
{
    public abstract class Mesh
    {
        public int XNodes = 10;
        public int YNodes = 10;

        public double[,] condictivities;
        
        public Mesh()
        {
            condictivities = new double[XNodes, YNodes];
        }
    }
}
