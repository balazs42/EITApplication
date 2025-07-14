namespace Utility.Classes.Measurement
{
    /// <summary>
    /// The boundary condition define that on which electrodes we specify the 
    /// voltages and currents used during measurement. This can be used for a CEM
    /// type forward simulation step.
    /// </summary>
    public sealed class BoundaryCondition
    {
        public List<Electrode> Electrodes { get; set; } = [];
        public int NumElectrodes { get; private set; } = -1;
        public int GroundElectrodeId { get; private set; } = -1;
        public int ExcitationElectrodeId { get; private set; } = -1;

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
                    if (kvp.Key == Electrodes[i].MeshId)
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


        private void Initialize(List<Electrode> electrodes)
        {
            Electrodes = electrodes;
            NumElectrodes = electrodes.Count;

            var groundElectrode = Electrodes.Find(x => x.IsGround);
            var excitationElectrode = Electrodes.Find(x => x.IsExcitation);

            if (groundElectrode == null || excitationElectrode == null)
                throw new ArgumentNullException("No ground or excitation id specified on electrodes, check calling code!");

            GroundElectrodeId = groundElectrode.Id;
            ExcitationElectrodeId = excitationElectrode.Id;
        }

        #region Getter and Setter Methods

        public List<double> GetElectrodeCurrentsList() => Electrodes.Select(x => x.Current).ToList();

        public List<double> GetElectrodePotentialsList() => Electrodes.Select(x => x.Potential).ToList();

        public double[] GetElectrodeCurrents()
        {
            double[] currents = new double[NumElectrodes];
            for (int i = 0; i < NumElectrodes; i++)
                currents[i] = Electrodes[i].Current;

            return currents;
        }

        public double[] GetElectrodePotentials()
        {
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
                Electrodes[i].Current = potentials[i];
        }

        private void SetElectrodePotentials(double[] potentials)
        {
            if (potentials.Length != Electrodes.Count)
                throw new ArgumentOutOfRangeException("Cannot set currents, item count mismatch between potentials and electrodes. Check code!");
            for (int i = 0; i < Electrodes.Count; i++)
                Electrodes[i].Current = potentials[i];
        }
        #endregion
    }
}
