namespace Utility.Classes.Meshing
{
    /// <summary>
    /// General mesh descriptor that can be used in both cases of the meshes
    /// </summary>
    public struct DiscretizationParameters
    {
        public DiscretizationType MeshType { get; set; }
        public int Layers { get; set; }
        public int BoundaryFEMVertexCount { get; set; }
        public int ElectrodeCount { get; set; }
        public int Nx { get; set; }
        public int Ny { get; set; }
        public List<Dictionary<int, double>> Inhomogenities { get; set; }
        public int Radius { get; set; }
    }
}
