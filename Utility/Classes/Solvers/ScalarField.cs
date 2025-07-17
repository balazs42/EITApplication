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

        public ScalarField()
        {
            IdValuePairs = new Dictionary<int, double>();
        }

        public ScalarField(Dictionary<int, double> idValuePairs)
        {
            IdValuePairs = idValuePairs;
        }

        public double GetValue(int key) => IdValuePairs[key];
        public void SetValue(int key, double value) => IdValuePairs[key] = value;
        public Dictionary<int, double> Get() => IdValuePairs;
        public void Set(Dictionary<int, double> idValuePairs) => IdValuePairs = idValuePairs;
    }
}
