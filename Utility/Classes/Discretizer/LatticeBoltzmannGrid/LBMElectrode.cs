namespace Utility.Classes.Discretizer.LatticeBoltzmannGrid
{
    public class LBMElectrode : Electrode
    {
        public int GridId = -1;

        public LBMElectrode(int gridId, double current, double potential)
        {
            GridId = gridId;
            Current = current;
            Potential = potential;
        }

        public LBMElectrode(int gridId, double current, double potential, double contactImpedance, bool isExcitation = false, bool isGround = false, bool isMeasuring = false)
        {
            GridId = gridId;   
            Current = current;
            Potential = potential;
            ZContact = contactImpedance;
            IsExcitation = isExcitation;
            IsGround = isGround;
            IsMeasuring = isMeasuring;
        }

        public LBMElectrode(int id, int gridId, double current, double potential, double contactImpedance, bool isExcitation = false, bool isGround = false, bool isMeasuring = false)
        {
            Id = id;
            GridId = gridId;
            Current = current;
            Potential = potential;
            ZContact = contactImpedance;
            IsExcitation = isExcitation;
            IsGround = isGround;
            IsMeasuring = isMeasuring;
        }
    }
}
