namespace Utility.Classes.Discretizer
{
    public abstract class Electrode
    {
        public int Id { get; set; } // Logical id associated to the electrode.

        // Injected current in ampere (positive: leaving domain).</summary>
        public double Current { get; set; }

        // Contact impedance (Ω).  0 → voltage measured directly
        // Common phantom / thoracic values: ~ 0.01 Ω – 0.1 Ω.
        public double ZContact { get; set; } = 0.1;

        // Measured voltage value on the electrode
        public double Potential { get; set; }

        public bool IsExcitation { get; set; } = false;
        public bool IsGround { get; set; } = false;
        public bool IsMeasuring { get; set; } = false;

        /// <summary>
        /// Indicates whether the electrode is a virtual (synthetic) contact generated between
        /// two physical electrodes. Virtual electrodes never act as excitation/ground sources
        /// but participate in measurement completion workflows.
        /// </summary>
        public bool IsVirtual { get; set; } = false;

        public bool IsSource => (IsGround || IsExcitation);

        public Electrode()
        {

        }
    }
}
