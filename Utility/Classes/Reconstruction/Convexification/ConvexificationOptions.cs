namespace Utility.Classes.Reconstruction.Convexification
{
    /// <summary>
    /// Compact configuration object for the convexification reconstruction path.
    /// The defaults are intentionally conservative because the fixed-point solve
    /// implemented on the current FEM basis is a practical surrogate of the
    /// chapter's stronger H2/C1 setting.
    /// </summary>
    public sealed class ConvexificationOptions
    {
        /// <summary>Carleman phase strength lambda in exp(2 lambda omega.x).</summary>
        public double Lambda { get; set; } = 1.25;

        /// <summary>Stabilization weight applied to the gradient regularization term.</summary>
        public double Beta { get; set; } = 1e-3;

        /// <summary>Closure parameter in s = r - epsilon w.</summary>
        public double Epsilon { get; set; } = 0.5;

        /// <summary>Lower positivity floor requested by the shifted boundary proxy.</summary>
        public double D0 { get; set; } = 0.25;

        /// <summary>Extra safety offset added after enforcing the positivity floor.</summary>
        public double PositivityMargin { get; set; } = 1e-3;

        /// <summary>Shared Dirichlet penalty weight used for both r and s.</summary>
        public double BoundaryDirichletWeight { get; set; } = 5.0;

        /// <summary>Shared Neumann penalty weight used for both r and s.</summary>
        public double BoundaryNeumannWeight { get; set; } = 1.0;

        /// <summary>
        /// Damping factor used when blending the latest fixed-point update.
        /// Values in (0, 1] are recommended.
        /// </summary>
        public double StepSize { get; set; } = 0.65;

        /// <summary>Maximum number of fixed-point iterations per reconstruction cycle.</summary>
        public int MaxIterations { get; set; } = 24;

        /// <summary>Relative objective tolerance for convergence.</summary>
        public double Tolerance { get; set; } = 1e-5;

        /// <summary>
        /// When true and a full drive cycle is available, drive derivatives wrap
        /// periodically instead of using one-sided endpoint differences.
        /// </summary>
        public bool UsePeriodicDriveDerivative { get; set; } = true;

        /// <summary>
        /// Controls whether every electrode participates in the Neumann penalty.
        /// Keeping this true matches the practical default requested by the task.
        /// </summary>
        public bool UseAllElectrodesForNeumannPenalty { get; set; } = true;

        /// <summary>Preferred Carleman direction omega. Defaults to the x-axis.</summary>
        public double[] Omega { get; set; } = new[] { 1.0, 0.0 };

        /// <summary>Whether the eliminated coefficient a(x) is averaged over the full cycle.</summary>
        public bool AverageRecoveredCoefficientAcrossCycle { get; set; } = true;

        /// <summary>Factor used by the backtracking line search in the fixed-point updates.</summary>
        public double LineSearchDecay { get; set; } = 0.5;

        /// <summary>Smallest admissible damping factor before the line search gives up.</summary>
        public double MinimumStepSize { get; set; } = 1e-3;

        /// <summary>Fallback lower bound for electrode lengths that are missing or degenerate.</summary>
        public double ElectrodeLengthFloor { get; set; } = 1e-6;

        /// <summary>Threshold above which positivity shifts are reported as warnings.</summary>
        public double LargeShiftWarningThreshold { get; set; } = 0.5;

        /// <summary>
        /// Tikhonov-style diagonal used in surrogate Poisson/reaction solves to
        /// stabilise singular or nearly singular systems.
        /// </summary>
        public double SigmaRecoveryRegularization { get; set; } = 1e-6;

        /// <summary>Small positive floor enforced on the recovered scale field V.</summary>
        public double MinimumScale { get; set; } = 1e-3;
    }
}
