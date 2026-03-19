using System.Diagnostics;
using Utility.Classes.Solvers;

namespace Utility.Classes
{
    public sealed class ConductivityDistribution : ScalarField
    {
        private Dictionary<int, double>? _conductivities;
        private double[]? _denseConductivities;
        private float[]? _denseConductivitiesCompact;
        private int _denseMinKey;

        public override Dictionary<int, double> IdValuePairs
        {
            get => Conductivities;
            set => Set(value);
        }

        public Dictionary<int, double> Conductivities
        {
            get => _conductivities ??= MaterializeDenseConductivities();
            set => Set(value);
        }

        public ConductivityDistribution(Dictionary<int, double> conductivities)
        {
            Set(conductivities);
        }

        private ConductivityDistribution(float[] denseConductivitiesCompact, int denseMinKey)
        {
            _conductivities = null;
            _denseConductivities = null;
            _denseConductivitiesCompact = denseConductivitiesCompact ?? Array.Empty<float>();
            _denseMinKey = denseMinKey;
        }

        /// <summary>
        /// Safely retrieves the conductivity for a given element ID.
        /// </summary>
        /// <param name="elementId">The unique ID of the element.</param>
        /// <returns>The conductivity of the element if found; otherwise, returns 0.0.</returns>
        public double GetConductivity(int elementId)
        {
            if (_denseConductivities != null)
            {
                int index = elementId - _denseMinKey;
                return (uint)index < (uint)_denseConductivities.Length ? _denseConductivities[index] : 0.0;
            }

            if (_denseConductivitiesCompact != null)
            {
                int index = elementId - _denseMinKey;
                return (uint)index < (uint)_denseConductivitiesCompact.Length ? _denseConductivitiesCompact[index] : 0.0;
            }

            return _conductivities != null && _conductivities.TryGetValue(elementId, out double conductivity) ? conductivity : 0.0;
        }

        /// <summary>
        /// Helper to convert this conductivity distribution into a format that
        /// can be used by operators expecting a potential distribution.
        /// </summary>
        public PotentialDistribution ToPotentialDistribution()
        {
            return new PotentialDistribution(this.Conductivities);
        }

        public void LogDistribution(int nx = 15, int ny = 15)
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny - 1; j++)
                    Debug.Write($"{Conductivities[i * nx + j].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)},");
                Debug.Write($"{Conductivities[i * nx + ny - 1].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)};\n");
            }
        }

        public override Dictionary<int,double> Get() => Conductivities;
        public override void Set(Dictionary<int, double> conductivites)
        {
            if (conductivites == null || conductivites.Count == 0)
            {
                _conductivities = new Dictionary<int, double>();
                _denseConductivities = null;
                _denseConductivitiesCompact = null;
                _denseMinKey = 0;
                return;
            }

            if (TryCreateDenseStorage(conductivites, out var denseValues, out int minKey))
            {
                _denseConductivities = denseValues;
                _denseConductivitiesCompact = null;
                _denseMinKey = minKey;
                _conductivities = null;
                return;
            }

            _denseConductivities = null;
            _denseConductivitiesCompact = null;
            _denseMinKey = 0;
            _conductivities = new Dictionary<int, double>(conductivites);
        }
        public override double GetValue(int key) => GetConductivity(key);
        public override void SetValue(int key, double value)
        {
            if (_denseConductivities != null)
            {
                int index = key - _denseMinKey;
                if ((uint)index < (uint)_denseConductivities.Length)
                {
                    _denseConductivities[index] = value;
                    return;
                }

                _conductivities = MaterializeDenseConductivities();
                _denseConductivities = null;
                _denseConductivitiesCompact = null;
                _denseMinKey = 0;
            }

            if (_denseConductivitiesCompact != null)
            {
                int index = key - _denseMinKey;
                if ((uint)index < (uint)_denseConductivitiesCompact.Length)
                {
                    _denseConductivitiesCompact[index] = (float)value;
                    return;
                }

                _conductivities = MaterializeDenseConductivities();
                _denseConductivitiesCompact = null;
                _denseMinKey = 0;
            }

            (_conductivities ??= new Dictionary<int, double>())[key] = value;
        }

        public ConductivityDistribution CreateCompactHistoryClone()
        {
            if (_denseConductivitiesCompact != null)
                return new ConductivityDistribution((float[])_denseConductivitiesCompact.Clone(), _denseMinKey);

            if (_denseConductivities != null)
            {
                var compact = new float[_denseConductivities.Length];
                for (int i = 0; i < compact.Length; i++)
                    compact[i] = (float)_denseConductivities[i];

                return new ConductivityDistribution(compact, _denseMinKey);
            }

            if (_conductivities == null || _conductivities.Count == 0)
                return new ConductivityDistribution([]);

            if (TryCreateDenseStorage(_conductivities, out var denseValues, out int minKey))
            {
                var compact = new float[denseValues.Length];
                for (int i = 0; i < compact.Length; i++)
                    compact[i] = (float)denseValues[i];

                return new ConductivityDistribution(compact, minKey);
            }

            return new ConductivityDistribution(new Dictionary<int, double>(_conductivities));
        }

        private Dictionary<int, double> MaterializeDenseConductivities()
        {
            if (_denseConductivities == null && _denseConductivitiesCompact == null)
                return _conductivities ?? new Dictionary<int, double>();

            int length = _denseConductivities?.Length ?? _denseConductivitiesCompact?.Length ?? 0;
            var materialized = new Dictionary<int, double>(length);
            if (_denseConductivities != null)
            {
                for (int i = 0; i < _denseConductivities.Length; i++)
                    materialized[_denseMinKey + i] = _denseConductivities[i];
            }
            else if (_denseConductivitiesCompact != null)
            {
                for (int i = 0; i < _denseConductivitiesCompact.Length; i++)
                    materialized[_denseMinKey + i] = _denseConductivitiesCompact[i];
            }

            _denseConductivities = null;
            _denseConductivitiesCompact = null;
            _denseMinKey = 0;
            return materialized;
        }

        private static bool TryCreateDenseStorage(Dictionary<int, double> values, out double[] denseValues, out int minKey)
        {
            minKey = 0;
            denseValues = Array.Empty<double>();

            if (values.Count == 0)
                return false;

            int min = int.MaxValue;
            int max = int.MinValue;
            foreach (var key in values.Keys)
            {
                if (key < min)
                    min = key;
                if (key > max)
                    max = key;
            }

            long expectedCount = (long)max - min + 1;
            if (expectedCount != values.Count || expectedCount <= 0 || expectedCount > int.MaxValue)
                return false;

            denseValues = new double[values.Count];
            foreach (var pair in values)
                denseValues[pair.Key - min] = pair.Value;

            minKey = min;
            return true;
        }

    }
}
