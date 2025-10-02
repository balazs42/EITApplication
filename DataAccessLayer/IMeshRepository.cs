using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;

namespace DataAccessLayer
{
    public interface IMeshRepository
    {
        void SaveFEMMesh(FEMMesh mesh, string name);
        void SaveLBMGrid(LBMGrid grid, string name);
        FEMMesh LoadFEMMesh(string filePath);
        LBMGrid LoadLBMGrid(string filePath);
        IEnumerable<DiscretizationInfo> GetDiscretizationInfos();
        void DeleteMesh(string filePath);
        MatlabExportResult ExportFemMeshForMatlab(FEMMesh mesh, string name, DrivePattern drivePattern);
    }
}
