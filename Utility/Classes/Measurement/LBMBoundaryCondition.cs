using Utility.Classes.Meshing;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace Utility.Classes.Measurement
{
    public class LBMBoundaryCondition : BoundaryCondition
    {
        public new List<LBMElectrode> Electrodes = [];
        public bool IsNeumann = false;


        public LBMBoundaryCondition(List<LBMElectrode> electrodes)
        {
            Electrodes = electrodes;

            Init(electrodes);
        }

        public void Init(List<LBMElectrode> electrodes)
        {
            Electrodes = electrodes.Cast<LBMElectrode>().ToList();
            NumElectrodes = Electrodes.Count;

            var groundElectrode = Electrodes.Find(x => x.IsGround);
            var excitationElectrode = Electrodes.Find(x => x.IsExcitation);

            if (groundElectrode == null || excitationElectrode == null)
                throw new ArgumentNullException("No ground or excitation id specified on electrodes, check calling code!");

            GroundElectrodeId = groundElectrode.Id;
            ExcitationElectrodeId = excitationElectrode.Id;
        }

        public override void Initialize(List<Electrode> electrodes)
        {
            Init(electrodes.Cast<LBMElectrode>().ToList());
        }
    }
}
