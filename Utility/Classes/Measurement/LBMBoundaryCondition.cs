using System.Diagnostics;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace Utility.Classes.Measurement
{
    public class LBMBoundaryCondition : BoundaryCondition<LBMElectrode>
    {
        public bool IsNeumann = false;

        public LBMBoundaryCondition(List<LBMElectrode> electrodes)
        {
            InitLBM(electrodes);
        }

        public void InitLBM(List<LBMElectrode> electrodes)
        {
            SetElectrodes(electrodes.Cast<LBMElectrode>().ToList());
            NumElectrodes = _electrodes.Count;

            var groundElectrode = _electrodes.Find(x => x.IsGround);
            var excitationElectrode = _electrodes.Find(x => x.IsExcitation);

            if (groundElectrode == null || excitationElectrode == null)
            {
                _electrodes[0].IsGround = true;
                _electrodes[0].Current = -1.0;
                _electrodes[1].IsExcitation = true;
                _electrodes[1].Current = 1.0;
                groundElectrode = _electrodes[0];
                excitationElectrode = _electrodes[1];

                Debug.WriteLine("No ground or excitation id specified on electrodes, setting to default!");
            }

            GroundElectrodeId = groundElectrode.Id;
            ExcitationElectrodeId = excitationElectrode.Id;
        }

        public override void Initialize(IEnumerable<Electrode> electrodes)
        {
            SetElectrodes(electrodes.Cast<LBMElectrode>().ToList());
        }
    }
}
