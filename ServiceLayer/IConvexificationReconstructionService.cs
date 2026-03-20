namespace ServiceLayer
{
    /// <summary>
    /// Service contract for the convexification reconstruction path.
    /// The parameterless <see cref="Initialize"/> helper mirrors the way the
    /// reconstruction page resolves runtime state from the workspace.
    /// </summary>
    public interface IConvexificationReconstructionService : IReconstructionService
    {
        void Initialize();
    }
}
