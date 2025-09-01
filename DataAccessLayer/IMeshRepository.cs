using System.Collections.Generic;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace DataAccessLayer
{
    public interface IMeshRepository
    {
        void SaveFEMMesh(FEMMesh mesh, string name);
        void SaveLBMMesh(LBMMesh mesh, string name);
        FEMMesh LoadFEMMesh(string filePath);
        LBMMesh LoadLBMMesh(string filePath);
        IEnumerable<MeshInfo> GetMeshes();
    }
}
