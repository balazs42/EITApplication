namespace Utility.Classes.Solvers
{
    /// <summary>
    /// Represents a 2D vector field defined on a mesh, mapping an integer ID
    /// (e.g., a FEMVertexId or ElementId) to a 2D vector.
    /// </summary>
    public sealed class VectorField
    {
        /// <summary>
        /// The underlying data mapping an ID to a vector (represented by a tuple).
        /// </summary>
        public IReadOnlyDictionary<int, (double X, double Y)> Data { get; }

        /// <summary>
        /// Creates a vector field backed by the provided data dictionary.
        /// </summary>
        public VectorField(IReadOnlyDictionary<int, (double X, double Y)> data)
        {
            Data = data;
        }

        /// <summary>
        /// Gets the vector at a specific ID, returning a zero vector if not found.
        /// </summary>
        public (double X, double Y) GetVector(int id)
        {
            return Data.TryGetValue(id, out var vector) ? vector : (0.0, 0.0);
        }
    }
}
