namespace Utility.Classes
{
    public sealed class ForwardSolution
    {
        public double[] SurfaceVoltages { get; init; }   // Ne values
        public double[] NodePotentials { get; init; }   // length = mesh.Vertices.Count
        /* possibly also CurrentDensity[] etc. */

        public ForwardSolution()
        {
            SurfaceVoltages = new double[0];
            NodePotentials = new double[0];
        }

        public ForwardSolution(double[] surfaceVoltages, double[] nodePotentials)
        {
            SurfaceVoltages = surfaceVoltages;
            NodePotentials = nodePotentials;
        }
    }
}
