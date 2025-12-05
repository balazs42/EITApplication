using System;
using System.Threading.Tasks;
using Utility.Classes;

namespace ServiceLayer
{
    /// <summary>
    /// Service contract that orchestrates block-based FEM reconstruction using
    /// the experimental block configuration pipeline. Implementations prepare
    /// measurements, forward intermediate frames and surface aggregated results
    /// to the UI.
    /// </summary>
    public interface IBlockFemReconstructionService
    {
        /// <summary>
        /// Raised when a full reconstruction result has been produced from a cycle
        /// of inverse iterations.
        /// </summary>
        event EventHandler<ReconstructionResult> ReconstructionUpdated;

        /// <summary>
        /// Raised for every intermediate reconstruction frame so the UI can update
        /// live gradient and field visualisations.
        /// </summary>
        event EventHandler<ReconstructionFrame> ReconstructionFrameUpdated;

        /// <summary>
        /// Controls whether intermediate frames are propagated to listeners during a run.
        /// </summary>
        bool VisualizeIterations { get; set; }

        /// <summary>
        /// Initializes the service based on the current <see cref="Utility.Classes.Application.Workspace"/>
        /// block configuration and discretization.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Executes a full reconstruction cycle using all prepared measurement frames.
        /// </summary>
        /// <param name="stepSize">Gradient descent step size.</param>
        /// <param name="regularizationWeight">Weighting applied to the regularization gradient.</param>
        /// <param name="excitationAmplitude">Excitation current amplitude for the measurement frames.</param>
        /// <returns>The aggregated reconstruction result for the cycle.</returns>
        Task<ReconstructionResult?> RunFullReconstructionCycleAsync(double stepSize,
                                                                     double regularizationWeight,
                                                                     double excitationAmplitude);

        /// <summary>
        /// Performs a single reconstruction step. Intermediate frames are emitted immediately
        /// and a reconstruction result is only produced when the drive-pattern cycle completes.
        /// </summary>
        /// <param name="stepSize">Gradient descent step size.</param>
        /// <param name="regularizationWeight">Weighting applied to the regularization gradient.</param>
        /// <param name="excitationAmplitude">Excitation current amplitude for the measurement frames.</param>
        /// <returns>The aggregated reconstruction result for the cycle.</returns>
        Task<ReconstructionResult?> StepReconstructionAsync(double stepSize,
                                                             double regularizationWeight,
                                                             double excitationAmplitude);
    }
}
