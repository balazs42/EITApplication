namespace Utility.Classes.ReconstructionParameters
{
    public enum RegularizationTechnique
    {
        None = 0,
        ZeroOrderTikhonov = 1,
        FirstOrderTikhonov = 2,
        TotalVariation = 3,
        Laplace = 4
    };
}
