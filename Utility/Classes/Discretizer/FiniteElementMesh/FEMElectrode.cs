using System;
using System.Collections.Generic;
using System.Linq;

namespace Utility.Classes.Discretizer.FiniteElementMesh
{
    public class FEMElectrode : Electrode
    {
        /// <summary>
        /// Global index of the representative mesh vertex for the electrode.
        /// </summary>
        public int MeshId { get; }

        /// <summary>
        /// Ordered list of FEM vertex ids that belong to the electrode patch.
        /// </summary>
        public List<int> FEMVertexIds { get; } = [];

        /// <summary>
        /// Physical length of the electrode contact region on the boundary.
        /// </summary>
        public double Length { get; set; } = 0.3;

        /// <summary>
        /// Indicates whether the electrode is interpreted as a single-node contact.
        /// </summary>
        public bool PointElectrode { get; set; } = true;

        public FEMElectrode(int meshId, List<int> femVertexIds, double current = double.NaN, double zContact = 0.1, double voltage = double.NaN, bool pointElectrode = true)
        {
            MeshId = meshId;
            Current = current;
            ZContact = zContact;
            Potential = voltage;
            if (femVertexIds != null)
                FEMVertexIds.AddRange(femVertexIds);
            PointElectrode = pointElectrode;
        }

        public FEMElectrode(int id, int meshId, double current, double zContact, double voltage, bool isExcitation = false, bool isGround = false, bool isMeasuring = false, bool pointElectrode = true)
        {
            Id = id;
            MeshId = meshId;
            Current = current;
            ZContact = zContact;
            Potential = voltage;
            IsExcitation = isExcitation;
            IsGround = isGround;
            IsMeasuring = isMeasuring;
            PointElectrode = pointElectrode;
        }

        public FEMElectrode(int id, IEnumerable<int> femVertexIds, double current = double.NaN, double zContact = 0.1, double voltage = double.NaN, bool isExcitation = false, bool isGround = false, bool isMeasuring = false)
        {
            if (femVertexIds == null)
                throw new ArgumentNullException(nameof(femVertexIds));

            var ids = femVertexIds.ToList();
            if (ids.Count == 0)
                throw new ArgumentException("At least one FEM vertex id must be provided.", nameof(femVertexIds));

            Id = id;
            MeshId = ids[0];
            Current = current;
            ZContact = zContact;
            Potential = voltage;
            IsExcitation = isExcitation;
            IsGround = isGround;
            IsMeasuring = isMeasuring;
            PointElectrode = ids.Count == 1;
            FEMVertexIds.AddRange(ids);
        }
    }
}
