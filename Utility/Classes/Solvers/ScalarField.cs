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

        public abstract double GetValue(int key);
        public abstract void SetValue(int key, double value);
        public abstract Dictionary<int, double> Get();
        public abstract void Set(Dictionary<int, double> idValuePairs);
    }
}
