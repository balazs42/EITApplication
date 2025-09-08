using Utility.Classes.Discretizer;

namespace Utility.Classes.Measurement
{
    public abstract class BoundaryCondition
    {
        public int NumElectrodes { get; set; } = -1;
        public int GroundElectrodeId { get; set; } = -1;
        public int ExcitationElectrodeId { get; set; } = -1;

        public abstract List<double> GetElectrodeCurrentsList();
        public abstract List<double> GetElectrodePotentialsList();
        public abstract double[] GetElectrodeCurrents();
        public abstract double[] GetElectrodePotentials();

        public abstract void SetElectrodeCurrents(IEnumerable<double> currents);
        public abstract void SetElectrodeCurrents(double[] currents);
        public abstract void SetElectrodePotentials(IEnumerable<double> potentials);
        public abstract void SetElectrodePotentials(double[] potentials);

        public abstract void Initialize(IEnumerable<Electrode> electrodes);
    }

    public abstract class BoundaryCondition<TElectrode> : BoundaryCondition
        where TElectrode : Electrode
    {
        protected readonly List<TElectrode> _electrodes = [];
        public IReadOnlyList<TElectrode> ElectrodesTyped => _electrodes;

        #region Constructors
        public BoundaryCondition()
        {

        }

        public BoundaryCondition(IEnumerable<Electrode> electrodes)
        {
            Initialize(electrodes);
        }

        public BoundaryCondition(IEnumerable<Electrode> electrodes, PotentialDistribution potentialDistribution)
        {
            Initialize(electrodes);

            for (int i = 0; i < NumElectrodes; i++)
            {
                foreach (var kvp in potentialDistribution.Potentials)
                {
                    if (kvp.Key == _electrodes.ElementAt(i).Id)
                    {
                        _electrodes.ElementAt(i).Potential = kvp.Value;
                        break;
                    }
                }
            }
        }

        public BoundaryCondition(IEnumerable<Electrode> electrodes, IEnumerable<double> potentials, IEnumerable<double> currents)
        {
            Initialize(electrodes);

            SetElectrodeCurrents(currents);
            SetElectrodePotentials(potentials);
        }

        public BoundaryCondition(IEnumerable<Electrode> electrodes, double[] potentials, double[] currents)
        {
            Initialize(electrodes);

            SetElectrodeCurrents(currents);
            SetElectrodePotentials(potentials);
        }

        public BoundaryCondition(IEnumerable<Electrode> electrodes, double[] currents)
        {
            Initialize(electrodes);

            SetElectrodeCurrents(currents);
        }

        public BoundaryCondition(IEnumerable<Electrode> electrodes, IEnumerable<double> currents)
        {
            Initialize(electrodes);

            SetElectrodeCurrents(currents);
        }
        #endregion

        #region Getter and Setter Methods
        public void SetElectrodes(IList<TElectrode> electrodes)
        {
            _electrodes.Clear();

            foreach (var el in electrodes)
                _electrodes.Add(el);
        }

        public List<TElectrode> GetElectrodes() => _electrodes;
        public override List<double> GetElectrodeCurrentsList() => [.. _electrodes.Select(x => x.Current)];
        public override List<double> GetElectrodePotentialsList() => [.. _electrodes.Select(x => x.Potential)];

        public override double[] GetElectrodeCurrents()
        {
            NumElectrodes = _electrodes.Count;
            double[] currents = new double[NumElectrodes];
            for (int i = 0; i < NumElectrodes; i++)
                currents[i] = _electrodes.ElementAt(i).Current;

            return currents;
        }

        public override double[] GetElectrodePotentials()
        {
            NumElectrodes = _electrodes.Count;
            double[] potentials = new double[NumElectrodes];
            for (int i = 0; i < NumElectrodes; i++)
                potentials[i] = _electrodes.ElementAt(i).Potential;

            return potentials;
        }

        public override void SetElectrodeCurrents(IEnumerable<double> currents)
        {
            NumElectrodes = _electrodes.Count;
            if (currents.Count() != NumElectrodes)
                throw new ArgumentOutOfRangeException("Cannot set currents, item count mismatch between currents and electrodes. Check code!");

            for (int i = 0; i < NumElectrodes; i++)
                _electrodes.ElementAt(i).Current = currents.ElementAt(i);
        }

        public override void SetElectrodeCurrents(double[] currents)
        {
            NumElectrodes = _electrodes.Count;
            if (currents.Length != NumElectrodes)
                throw new ArgumentOutOfRangeException("Cannot set currents, item count mismatch between currents and electrodes. Check code!");
            for (int i = 0; i < NumElectrodes; i++)
                _electrodes.ElementAt(i).Current = currents[i];
        }

        public override void SetElectrodePotentials(IEnumerable<double> potentials)
        {
            NumElectrodes = _electrodes.Count;
            if (potentials.Count() != NumElectrodes)
                throw new ArgumentOutOfRangeException("Cannot set potentials, item count mismatch between potentials and electrodes. Check code!");

            for (int i = 0; i < NumElectrodes; i++)
                _electrodes.ElementAt(i).Potential = potentials.ElementAt(i);
        }

        public override void SetElectrodePotentials(double[] potentials)
        {
            NumElectrodes = _electrodes.Count;
            if (potentials.Length != NumElectrodes)
                throw new ArgumentOutOfRangeException("Cannot set potentials, item count mismatch between potentials and electrodes. Check code!");
            for (int i = 0; i < NumElectrodes; i++)
                _electrodes.ElementAt(i).Potential = potentials[i];
        }
        #endregion
    }
}
