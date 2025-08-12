using System.Diagnostics;
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
            base.Electrodes = electrodes.Cast<LBMElectrode>().ToList();
            InitLBM(electrodes);
        }

        public void InitLBM(List<LBMElectrode> electrodes)
        {
            Electrodes = electrodes.Cast<LBMElectrode>().ToList();
            NumElectrodes = Electrodes.Count;

            var groundElectrode = Electrodes.Find(x => x.IsGround);
            var excitationElectrode = Electrodes.Find(x => x.IsExcitation);

            if (groundElectrode == null || excitationElectrode == null)
            {
                Electrodes[0].IsGround = true;
                Electrodes[0].Current = -1.0;
                Electrodes[1].IsExcitation = true;
                Electrodes[1].Current = 1.0;
                groundElectrode = Electrodes[0];
                excitationElectrode = Electrodes[1];

                Debug.WriteLine("No ground or excitation id specified on electrodes, setting to default!");
            }

            GroundElectrodeId = groundElectrode.Id;
            ExcitationElectrodeId = excitationElectrode.Id;
            base.Electrodes = electrodes.Cast<LBMElectrode>().ToList();
        }

        public override void Initialize(IEnumerable<Electrode> electrodes)
        {
            InitLBM(electrodes.Cast<LBMElectrode>().ToList());
            base.Electrodes = electrodes.Cast<LBMElectrode>().ToList();
        }

        public List<LBMElectrode> GetElectrodes() => this.Electrodes;
    }
}
