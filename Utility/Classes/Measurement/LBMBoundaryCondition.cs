using System.Diagnostics;
using System.Linq;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Measurement
{
    public class LBMBoundaryCondition : BoundaryCondition<LBMElectrode>
    {
        public bool IsNeumann = false;

        public LBMBoundaryCondition(List<LBMElectrode> electrodes, bool requireDrivePair = true)
        {
            InitLBM(electrodes, requireDrivePair);
        }

        public void InitLBM(List<LBMElectrode> electrodes, bool requireDrivePair = true)
        {
            SetElectrodes([.. electrodes.Cast<LBMElectrode>()]);
            NumElectrodes = _electrodes.Count;

            var groundElectrode = _electrodes.Find(x => x.IsGround);
            var excitationElectrode = _electrodes.Find(x => x.IsExcitation);

            if (requireDrivePair && (groundElectrode == null || excitationElectrode == null) && _electrodes.Count >= 2)
            {
                _electrodes[0].IsGround = true;
                _electrodes[0].Current = -1.0;
                _electrodes[1].IsExcitation = true;
                _electrodes[1].Current = 1.0;
                groundElectrode = _electrodes[0];
                excitationElectrode = _electrodes[1];

                Debug.WriteLine("No ground or excitation id specified on electrodes, setting to default!");
            }

            GroundElectrodeId = groundElectrode?.Id ?? (_electrodes.Count > 0 ? _electrodes[0].Id : -1);
            ExcitationElectrodeId = excitationElectrode?.Id ?? (_electrodes.Count > 1 ? _electrodes[1].Id : GroundElectrodeId);
        }

        public override void Initialize(IEnumerable<Electrode> electrodes)
        {
            InitLBM([.. electrodes.Cast<LBMElectrode>()]);
        }
    }
}
