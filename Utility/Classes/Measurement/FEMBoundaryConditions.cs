using System.Diagnostics;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;

namespace Utility.Classes.Measurement
{
    /// <summary>
    /// The boundary condition define that on which electrodes we specify the 
    /// voltages and currents used during measurement. This can be used for a CEM
    /// type forward simulation step.
    /// </summary>
    public sealed class FEMBoundaryCondition : BoundaryCondition<FEMElectrode>
    {
        public FEMBoundaryCondition(List<FEMElectrode> electrodes)
        {
            InitFEM(electrodes);
        }

        public FEMBoundaryCondition(List<FEMElectrode> electrodes, PotentialDistribution potentialDistribution)
        {
            InitFEM(electrodes);

            for (int i = 0; i < NumElectrodes; i++)
            {
                foreach (var kvp in potentialDistribution.Potentials)
                {
                    if (kvp.Key == _electrodes[i].MeshId)
                    {
                        _electrodes[i].Potential = kvp.Value;
                        break;
                    }
                }
            }
        }

        public void InitFEM(List<FEMElectrode> electrodes)
        {
            SetElectrodes(electrodes.Cast<FEMElectrode>().ToList());
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
            InitFEM(electrodes.Cast<FEMElectrode>().ToList());
        }
    }
}
