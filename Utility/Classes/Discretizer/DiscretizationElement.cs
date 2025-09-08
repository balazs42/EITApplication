namespace Utility.Classes.Discretizer
{
    public abstract class DiscretizationElement
    {
        public int Id { get; set; } = -1;
        public double Conductivity { get; set; } = 1.0;
        public double Permittivity { get; set; } = 1.0;
    }
}