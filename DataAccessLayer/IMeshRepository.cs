using Utility.Classes.Meshing;
using System;

namespace DataAccessLayer
{
    public interface IMeshRepository
    {
        void SaveMesh(IMesh mesh, string name);
        IMesh LoadMesh(string name, DateTime savedAt);
    }
}
