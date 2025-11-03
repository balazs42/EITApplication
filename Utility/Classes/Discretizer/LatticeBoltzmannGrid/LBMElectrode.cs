namespace Utility.Classes.Discretizer.LatticeBoltzmannGrid
{
    public class LBMElectrode : Electrode
    {
        public int GridId = -1;

        public LBMElectrode(int gridId, double current, double potential, bool isVirtual = false)
        {
            GridId = gridId;
            Current = current;
            Potential = potential;
            IsVirtual = isVirtual;
        }

        public LBMElectrode(int gridId, double current, double potential, double contactImpedance, bool isExcitation = false, bool isGround = false, bool isMeasuring = false, bool isVirtual = false)
        {
            GridId = gridId;
            Current = current;
            Potential = potential;
            ZContact = contactImpedance;
            IsExcitation = isExcitation;
            IsGround = isGround;
            IsMeasuring = isMeasuring;
            IsVirtual = isVirtual;
        }

        public LBMElectrode(int id, int gridId, double current, double potential, double contactImpedance, bool isExcitation = false, bool isGround = false, bool isMeasuring = false, bool isVirtual = false)
        {
            Id = id;
            GridId = gridId;
            Current = current;
            Potential = potential;
            ZContact = contactImpedance;
            IsExcitation = isExcitation;
            IsGround = isGround;
            IsMeasuring = isMeasuring;
            IsVirtual = isVirtual;
        }
    }
}
