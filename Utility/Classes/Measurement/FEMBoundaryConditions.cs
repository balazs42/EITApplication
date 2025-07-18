using System.Diagnostics;
using System.Diagnostics.Tracing;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;

namespace Utility.Classes.Measurement
{
    /// <summary>
    /// The boundary condition define that on which electrodes we specify the 
    /// voltages and currents used during measurement. This can be used for a CEM
    /// type forward simulation step.
    /// </summary>
    public sealed class FEMBoundaryCondition : BoundaryCondition
    {
        public new List<FEMElectrode> Electrodes { get; set; } = [];


        public FEMBoundaryCondition(List<FEMElectrode> electrodes)
        {
            this.Init(electrodes);

            Electrodes = electrodes;
        }


        public FEMBoundaryCondition(List<FEMElectrode> electrodes, PotentialDistribution potentialDistribution)
        {
            this.Init(electrodes);

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

        public void Init(List<FEMElectrode> electrodes)
        {
            Electrodes = electrodes.Cast<FEMElectrode>().ToList();
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
        }

        public override void Initialize(List<Electrode> electrodes)
        {
            Init(electrodes.Cast<FEMElectrode>().ToList());
        }
    }
}
