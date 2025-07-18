using Utility.Classes.Meshing;

namespace Utility.Classes.Measurement
{
    public abstract class BoundaryCondition
    {
        public IEnumerable<Electrode> Electrodes { get; set; } = [];
        public int NumElectrodes { get; set; } = -1;
        public int GroundElectrodeId { get; set; } = -1;
        public int ExcitationElectrodeId { get; set; } = -1;

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
                    if (kvp.Key == Electrodes.ElementAt(i).Id)
                    {
                        Electrodes.ElementAt(i).Potential = kvp.Value;
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


        public abstract void Initialize(IEnumerable<Electrode> electrodes);

        #region Getter and Setter Methods

        public List<double> GetElectrodeCurrentsList() => Electrodes.Select(x => x.Current).ToList();

        public List<double> GetElectrodePotentialsList() => Electrodes.Select(x => x.Potential).ToList();

        public double[] GetElectrodeCurrents()
        {
            NumElectrodes = Electrodes.Count();
            double[] currents = new double[NumElectrodes];
            for (int i = 0; i < NumElectrodes; i++)
                currents[i] = Electrodes.ElementAt(i).Current;

            return currents;
        }

        public double[] GetElectrodePotentials()
        {
            NumElectrodes = Electrodes.Count();
            double[] potentials = new double[NumElectrodes];
            for (int i = 0; i < NumElectrodes; i++)
                potentials[i] = Electrodes.ElementAt(i).Potential;

            return potentials;
        }

        private void SetElectrodeCurrents(IEnumerable<double> currents)
        {
            NumElectrodes = Electrodes.Count();
            if (currents.Count() != NumElectrodes)
                throw new ArgumentOutOfRangeException("Cannot set currents, item count mismatch between currents and electrodes. Check code!");

            for (int i = 0; i < NumElectrodes; i++)
                Electrodes.ElementAt(i).Current = currents.ElementAt(i);
        }

        private void SetElectrodeCurrents(double[] currents)
        {
            NumElectrodes = Electrodes.Count();
            if (currents.Length != NumElectrodes)
                throw new ArgumentOutOfRangeException("Cannot set currents, item count mismatch between currents and electrodes. Check code!");
            for (int i = 0; i < NumElectrodes; i++)
                Electrodes.ElementAt(i).Current = currents[i];
        }

        private void SetElectrodePotentials(IEnumerable<double> potentials)
        {
            NumElectrodes = Electrodes.Count();
            if (potentials.Count() != NumElectrodes)
                throw new ArgumentOutOfRangeException("Cannot set potentials, item count mismatch between potentials and electrodes. Check code!");

            for (int i = 0; i < NumElectrodes; i++)
                Electrodes.ElementAt(i).Potential = potentials.ElementAt(i);
        }

        private void SetElectrodePotentials(double[] potentials)
        {
            NumElectrodes = Electrodes.Count();
            if (potentials.Length != NumElectrodes)
                throw new ArgumentOutOfRangeException("Cannot set potentials, item count mismatch between potentials and electrodes. Check code!");
            for (int i = 0; i < NumElectrodes; i++)
                Electrodes.ElementAt(i).Potential = potentials[i];
        }
        #endregion
    }
}
