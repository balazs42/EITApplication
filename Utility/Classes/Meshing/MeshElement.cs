namespace Utility.Classes.Meshing
{
    public abstract class MeshElement
    {
        public int Id { get; set; } = -1;
        public List<Vertex> Vertices = [];

        /// <summary>
        /// Gets or sets a value indicating whether this element is pinned to a fixed potential.
        /// </summary>
        public bool IsPinned { get; set; } = false;

        /// <summary>
        /// The value of the potential to which this element is pinned.
        /// </summary>
        public double PinValue { get; set; } = 0.0;
    }
}
