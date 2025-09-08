using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace DataAccessLayer
{
    public interface IMeshRepository
    {
        void SaveFEMMesh(FEMMesh mesh, string name);
        void SaveLBMGrid(LBMGrid grid, string name);
        FEMMesh LoadFEMMesh(string filePath);
        LBMGrid LoadLBMGrid(string filePath);
        IEnumerable<DiscretizationInfo> GetMeshes();
    }
}
