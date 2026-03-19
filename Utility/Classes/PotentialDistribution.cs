using System.Diagnostics;
using Utility.Classes.Solvers;
using System;

namespace Utility.Classes
{
    public class PotentialDistribution : ScalarField
    {
        private Dictionary<int, double>? _potentials;
        private double[]? _densePotentials;
        private float[]? _densePotentialsCompact;
        private int _denseMinKey;

        public override Dictionary<int, double> IdValuePairs
        {
            get => Potentials;
            set => Set(value);
        }

        // Maps FEMVertex.GlobalId to its potential value.
        public Dictionary<int, double> Potentials
        {
            get => _potentials ??= MaterializeDensePotentials();
            set => Set(value);
        }

        internal int Count
            => _densePotentials?.Length
               ?? _densePotentialsCompact?.Length
               ?? _potentials?.Count
               ?? 0;

        public PotentialDistribution(Dictionary<int, double> potentials)
        {
            Set(potentials);
        }

        internal PotentialDistribution(double[] densePotentials, int denseMinKey, bool takeOwnership)
        {
            _potentials = null;
            _densePotentials = takeOwnership ? densePotentials : (double[])densePotentials.Clone();
            _densePotentialsCompact = null;
            _denseMinKey = denseMinKey;
        }

        private PotentialDistribution(float[] densePotentialsCompact, int denseMinKey)
        {
            _potentials = null;
            _densePotentials = null;
            _densePotentialsCompact = densePotentialsCompact ?? Array.Empty<float>();
            _denseMinKey = denseMinKey;
        }

        public double GetPotential(int FEMVertexId)
        {
            if (_densePotentials != null)
            {
                int index = FEMVertexId - _denseMinKey;
                return (uint)index < (uint)_densePotentials.Length ? _densePotentials[index] : 0.0;
            }

            if (_densePotentialsCompact != null)
            {
                int index = FEMVertexId - _denseMinKey;
                return (uint)index < (uint)_densePotentialsCompact.Length ? _densePotentialsCompact[index] : 0.0;
            }

            return _potentials != null && _potentials.TryGetValue(FEMVertexId, out double potential) ? potential : 0.0;
        }

        public void LogDistribution(int nx = 25, int ny = 25)
        {
            for(int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny - 1; j++)
                    Debug.Write($"{Potentials[i * nx + j].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)},");
                Debug.Write($"{Potentials[i*nx + ny-1].ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)};\n");
            }
        }

        public override Dictionary<int, double> Get() => Potentials;
        public override void Set(Dictionary<int, double> potentials)
        {
            if (potentials == null || potentials.Count == 0)
            {
                _potentials = new Dictionary<int, double>();
                _densePotentials = null;
                _densePotentialsCompact = null;
                _denseMinKey = 0;
                return;
            }

            if (TryCreateDenseStorage(potentials, out var denseValues, out int minKey))
            {
                _densePotentials = denseValues;
                _densePotentialsCompact = null;
                _denseMinKey = minKey;
                _potentials = null;
                return;
            }

            _densePotentials = null;
            _densePotentialsCompact = null;
            _denseMinKey = 0;
            _potentials = new Dictionary<int, double>(potentials);
        }
        public override double GetValue(int key) => GetPotential(key);
        public override void SetValue(int key, double value)
        {
            if (_densePotentials != null)
            {
                int index = key - _denseMinKey;
                if ((uint)index < (uint)_densePotentials.Length)
                {
                    _densePotentials[index] = value;
                    return;
                }

                _potentials = MaterializeDensePotentials();
                _densePotentials = null;
                _densePotentialsCompact = null;
                _denseMinKey = 0;
            }

            if (_densePotentialsCompact != null)
            {
                int index = key - _denseMinKey;
                if ((uint)index < (uint)_densePotentialsCompact.Length)
                {
                    _densePotentialsCompact[index] = (float)value;
                    return;
                }

                _potentials = MaterializeDensePotentials();
                _densePotentialsCompact = null;
                _denseMinKey = 0;
            }

            (_potentials ??= new Dictionary<int, double>())[key] = value;
        }

        internal static PotentialDistribution FromDense(double[] densePotentials, int denseMinKey = 0, bool takeOwnership = false)
        {
            if (densePotentials == null)
                throw new ArgumentNullException(nameof(densePotentials));

            if (densePotentials.Length == 0)
                return new PotentialDistribution([]);

            return new PotentialDistribution(densePotentials, denseMinKey, takeOwnership);
        }

        internal bool TryGetDenseStorage(out double[]? densePotentials, out float[]? densePotentialsCompact, out int denseMinKey)
        {
            densePotentials = _densePotentials;
            densePotentialsCompact = _densePotentialsCompact;
            denseMinKey = _denseMinKey;
            return densePotentials != null || densePotentialsCompact != null;
        }

        public PotentialDistribution CreateCompactHistoryClone()
        {
            if (_densePotentialsCompact != null)
                return new PotentialDistribution((float[])_densePotentialsCompact.Clone(), _denseMinKey);

            if (_densePotentials != null)
            {
                var compact = new float[_densePotentials.Length];
                for (int i = 0; i < compact.Length; i++)
                    compact[i] = (float)_densePotentials[i];

                return new PotentialDistribution(compact, _denseMinKey);
            }

            if (_potentials == null || _potentials.Count == 0)
                return new PotentialDistribution([]);

            if (TryCreateDenseStorage(_potentials, out var denseValues, out int minKey))
            {
                var compact = new float[denseValues.Length];
                for (int i = 0; i < compact.Length; i++)
                    compact[i] = (float)denseValues[i];

                return new PotentialDistribution(compact, minKey);
            }

            return new PotentialDistribution(new Dictionary<int, double>(_potentials));
        }

        private Dictionary<int, double> MaterializeDensePotentials()
        {
            var densePotentials = _densePotentials;
            var densePotentialsCompact = _densePotentialsCompact;
            int denseMinKey = _denseMinKey;

            if (densePotentials == null && densePotentialsCompact == null)
                return _potentials ?? new Dictionary<int, double>();

            int length = densePotentials?.Length ?? densePotentialsCompact?.Length ?? 0;
            var materialized = new Dictionary<int, double>(length);
            if (densePotentials != null)
            {
                for (int i = 0; i < densePotentials.Length; i++)
                    materialized[denseMinKey + i] = densePotentials[i];
            }
            else if (densePotentialsCompact != null)
            {
                for (int i = 0; i < densePotentialsCompact.Length; i++)
                    materialized[denseMinKey + i] = densePotentialsCompact[i];
            }

            _densePotentials = null;
            _densePotentialsCompact = null;
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
