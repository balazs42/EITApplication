
using Utility.Classes.Meshing;

namespace Utility.Classes.Factories
{
    public static class MeshFactory
    {
        public static IMesh Create(MeshType mt) => mt switch
        {
            MeshType.FEM => new FEMMesh(),
            MeshType.LBM => new LBMMesh(),
            _ => throw new NotSupportedException()
        };
    }
}
