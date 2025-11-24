namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Enum representing the functional category of a processing block in the reconstruction pipeline.
    /// </summary>
    public enum BlockType
    {
        /// <summary>
        /// Block for setting up the initial conductivity distribution.
        /// Supports methods like Homogeneous, Takens' embedding, etc.
        /// </summary>
        Initialization,

        /// <summary>
        /// Block for the forward problem solver (e.g., FEM, LBM).
        /// </summary>
        Solver,

        /// <summary>
        /// Block for regularization techniques to handle ill-posedness.
        /// </summary>
        Regularizer,

        /// <summary>
        /// Block defining the error metric (misfit functional).
        /// </summary>
        ErrorMetric,

        /// <summary>
        /// Block for the numerical optimization strategy.
        /// </summary>
        Optimizer,

        /// <summary>
        /// Block for post-reconstruction image processing.
        /// </summary>
        PostProcessing
    }
}
