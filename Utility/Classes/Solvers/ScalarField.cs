namespace Utility.Classes.Solvers
{
    /// <summary>
    /// Represents a scalar field, by assigning mesh ids to values
    /// </summary>
    public abstract class ScalarField
    {
        /// <summary>
        /// The field represented by the dictionary
        /// </summary>
        public abstract Dictionary<int, double> IdValuePairs { get; set; }

        /// <summary>
        /// Creates an empty scalar field.
        /// </summary>
        public ScalarField()
        {
            IdValuePairs = new Dictionary<int, double>();
        }

        /// <summary>
        /// Creates a scalar field initialised with the provided mapping.
        /// </summary>
        public ScalarField(Dictionary<int, double> idValuePairs)
        {
            IdValuePairs = idValuePairs;
        }

        /// <summary>Retrieves the scalar value associated with the supplied identifier.</summary>
        public abstract double GetValue(int key);
        /// <summary>Sets the scalar value associated with the supplied identifier.</summary>
        public abstract void SetValue(int key, double value);
        /// <summary>Returns the full identifier-to-value mapping.</summary>
        public abstract Dictionary<int, double> Get();
        /// <summary>Replaces the underlying identifier-to-value mapping.</summary>
        public abstract void Set(Dictionary<int, double> idValuePairs);
    }
}
