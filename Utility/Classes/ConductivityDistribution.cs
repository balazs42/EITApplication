namespace Utility.Classes
{
    // Holds σ (conductivity) values assigned per element
    // The length of Values must equal Mesh.Elements.Count
    // whenever this is used in a forward or inverse solve.
    public sealed class ConductivityDistribution
    {
        private readonly double[] _values;

        public int Length => _values.Length;
        public double this[int elementId]
        {
            get => _values[elementId];
            set => _values[elementId] = value;
        }

        public ConductivityDistribution(int elementCount, double initial = 1.0)
            => _values = Enumerable.Repeat(initial, elementCount).ToArray();

        private ConductivityDistribution(double[] copyFrom)
            => _values = copyFrom.ToArray(); 

        public double[] ToArray() => _values.ToArray();
        public static ConductivityDistribution FromArray(double[] a)
            => new ConductivityDistribution(a);

        public ConductivityDistribution Clone() => FromArray(_values);
    }
}
