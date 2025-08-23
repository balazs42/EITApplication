using Utility.Classes;

namespace DataAccessLayer
{
    public interface IMeshRepository
    {
        void SaveMesh(IMesh mesh, string name);
        IMesh LoadMesh(string filePath);
    }
}
