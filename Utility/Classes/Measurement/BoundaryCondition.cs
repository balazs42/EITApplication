using Utility.Classes.Meshing;

namespace Utility.Classes.Measurement
{
    public abstract class BoundaryCondition
    {
        public List<Electrode> Electrodes { get; set; } = [];
        public int NumElectrodes { get; set; } = -1;
        public int GroundElectrodeId { get; set; } = -1;
        public int ExcitationElectrodeId { get; set; } = -1;

        public BoundaryCondition()
        {

        }

        public BoundaryCondition(List<Electrode> electrodes)
        {
            Initialize(electrodes);
        }

        public BoundaryCondition(List<Electrode> electrodes, PotentialDistribution potentialDistribution)
        {
            Initialize(electrodes);

            for (int i = 0; i < NumElectrodes; i++)
            {
                foreach (var kvp in potentialDistribution.Potentials)
                {
                    if (kvp.Key == Electrodes[i].Id)
                    {
                        Electrodes[i].Potential = kvp.Value;
                        break;
                    }
                }
            }
        }

        public BoundaryCondition(List<Electrode> electrodes, List<double> potentials, List<double> currents)
        {
            Initialize(electrodes);

            SetElectrodeCurrents(currents);
            SetElectrodePotentials(potentials);
        }

        public BoundaryCondition(List<Electrode> electrodes, double[] potentials, double[] currents)
        {
            Initialize(electrodes);

            SetElectrodeCurrents(currents);
            SetElectrodePotentials(potentials);
        }

        public BoundaryCondition(List<Electrode> electrodes, double[] currents)
        {
            Initialize(electrodes);

            SetElectrodeCurrents(currents);
        }

        public BoundaryCondition(List<Electrode> electrodes, List<double> currents)
        {
            Initialize(electrodes);

            SetElectrodeCurrents(currents);
        }


        public abstract void Initialize(List<Electrode> electrodes);

        #region Getter and Setter Methods

        public List<double> GetElectrodeCurrentsList() => Electrodes.Select(x => x.Current).ToList();

        public List<double> GetElectrodePotentialsList() => Electrodes.Select(x => x.Potential).ToList();

        public double[] GetElectrodeCurrents()
        {
            NumElectrodes = Electrodes.Count;
            double[] currents = new double[NumElectrodes];
            for (int i = 0; i < NumElectrodes; i++)
                currents[i] = Electrodes[i].Current;

            return currents;
        }

        public double[] GetElectrodePotentials()
        {
            NumElectrodes = Electrodes.Count;
            double[] potentials = new double[NumElectrodes];
            for (int i = 0; i < NumElectrodes; i++)
                potentials[i] = Electrodes[i].Potential;

            return potentials;
        }

        private void SetElectrodeCurrents(List<double> currents)
        {
            if (currents.Count != Electrodes.Count)
                throw new ArgumentOutOfRangeException("Cannot set currents, item count mismatch between currents and electrodes. Check code!");

            for (int i = 0; i < Electrodes.Count; i++)
                Electrodes[i].Current = currents[i];
        }

        private void SetElectrodeCurrents(double[] currents)
        {
            if (currents.Length != Electrodes.Count)
                throw new ArgumentOutOfRangeException("Cannot set currents, item count mismatch between currents and electrodes. Check code!");
            for (int i = 0; i < Electrodes.Count; i++)
                Electrodes[i].Current = currents[i];
        }

        private void SetElectrodePotentials(List<double> potentials)
        {
            if (potentials.Count != Electrodes.Count)
                throw new ArgumentOutOfRangeException("Cannot set potentials, item count mismatch between potentials and electrodes. Check code!");

            for (int i = 0; i < Electrodes.Count; i++)
                Electrodes[i].Potential = potentials[i];
        }

        private void SetElectrodePotentials(double[] potentials)
        {
            if (potentials.Length != Electrodes.Count)
                throw new ArgumentOutOfRangeException("Cannot set potentials, item count mismatch between potentials and electrodes. Check code!");
            for (int i = 0; i < Electrodes.Count; i++)
                Electrodes[i].Potential = potentials[i];
        }
        #endregion
    }
}
