namespace Utility.Classes.Reconstruction.Convexification
{
    /// <summary>
    /// Compact configuration object for the convexification reconstruction path.
    /// The defaults are intentionally conservative because the current solver
    /// minimizes a practical least-squares surrogate on a P1 FEM basis rather
    /// than the chapter's stronger H2/C1 setting.
    /// </summary>
    public sealed class ConvexificationOptions
    {
        /// <summary>Carleman phase strength lambda in exp(2 lambda omega.x).</summary>
        public double Lambda { get; set; } = 1.25;

        /// <summary>
        /// Relative weight of the Carleman-weighted interior residual compared
        /// with the electrode boundary penalties.
        /// </summary>
        public double InteriorResidualWeight { get; set; } = 8.0;

        /// <summary>Stabilization weight applied to the gradient regularization term.</summary>
        public double Beta { get; set; } = 2e-4;

        /// <summary>Closure parameter in s = r - epsilon w.</summary>
        public double Epsilon { get; set; } = 0.5;

        /// <summary>Lower positivity floor requested by the shifted boundary proxy.</summary>
        public double D0 { get; set; } = 0.25;

        /// <summary>Extra safety offset added after enforcing the positivity floor.</summary>
        public double PositivityMargin { get; set; } = 1e-3;

        /// <summary>Shared Dirichlet penalty weight used for both r and s.</summary>
        public double BoundaryDirichletWeight { get; set; } = 0.2;

        /// <summary>Shared Neumann penalty weight used for both r and s.</summary>
        public double BoundaryNeumannWeight { get; set; } = 0.04;

        /// <summary>
        /// Damping factor used when blending the latest objective-driven update.
        /// Values in (0, 1] are recommended.
        /// </summary>
        public double StepSize { get; set; } = 0.65;

        /// <summary>Maximum number of inner least-squares descent iterations per reconstruction cycle.</summary>
        public int MaxIterations { get; set; } = 24;

        /// <summary>Relative objective tolerance for convergence.</summary>
        public double Tolerance { get; set; } = 5e-6;

        /// <summary>
        /// Relative/absolute tolerance used when accepting a line-search candidate.
        /// This prevents numerical roundoff from rejecting a candidate whose
        /// objective differs only in the last few digits.
        /// </summary>
        public double ObjectiveAcceptanceTolerance { get; set; } = 1e-6;

        /// <summary>
        /// Additional relative acceptance tolerance for the practical
        /// preconditioned line search. The current P1-based surrogate is not an
        /// exact steepest-descent direction, so very small relative objective
        /// increases can still be treated as numerically stable updates.
        /// </summary>
        public double LineSearchRelativeTolerance { get; set; } = 5e-5;

        /// <summary>
        /// Norm threshold for the preconditioned descent field below which the
        /// inner convexification solve is treated as stationary.
        /// </summary>
        public double InnerGradientTolerance { get; set; } = 1e-6;

        /// <summary>
        /// Optional explicit outer-cycle count for the background run.
        /// Use 0 to fall back to the generic reconstruction iteration count.
        /// </summary>
        public int OuterIterations { get; set; } = 0;

        /// <summary>
        /// Relative outer-cycle tolerance used on both the objective value and
        /// the conductivity update norm before the background run stops early.
        /// </summary>
        public double OuterTolerance { get; set; } = 5e-4;

        /// <summary>
        /// When true and a full drive cycle is available, drive derivatives wrap
        /// periodically instead of using one-sided endpoint differences.
        /// </summary>
        public bool UsePeriodicDriveDerivative { get; set; } = true;

        /// <summary>
        /// Optional odd window size used to smooth electrode-wise drive signals
        /// before differentiation. Set to 0 or 1 to disable smoothing.
        /// </summary>
        public int DerivativeSmoothingWindow { get; set; } = 0;

        /// <summary>
        /// Number of repeated smoothing passes applied before differentiation.
        /// </summary>
        public int DerivativeSmoothingPasses { get; set; } = 0;

        /// <summary>
        /// When true and a full cycle is available, derivative smoothing also
        /// wraps periodically across the drive-pattern cycle.
        /// </summary>
        public bool UsePeriodicDerivativeSmoothing { get; set; } = true;

        /// <summary>
        /// Controls whether every electrode participates in the Neumann penalty.
        /// Keeping this true matches the practical default requested by the task.
        /// </summary>
        public bool UseAllElectrodesForNeumannPenalty { get; set; } = true;

        /// <summary>Preferred Carleman direction omega. Defaults to the x-axis.</summary>
        public double[] Omega { get; set; } = new[] { 1.0, 0.0 };

        /// <summary>Whether the eliminated coefficient a(x) is averaged over the full cycle.</summary>
        public bool AverageRecoveredCoefficientAcrossCycle { get; set; } = true;

        /// <summary>
        /// Smoothing weight applied to the recovered coefficient field before
        /// the V-stage quasi-reversibility solve.
        /// </summary>
        public double CoefficientSmoothingWeight { get; set; } = 0.02;

        /// <summary>Factor used by the backtracking line search in the inner least-squares updates.</summary>
        public double LineSearchDecay { get; set; } = 0.5;

        /// <summary>Smallest admissible damping factor before the line search gives up.</summary>
        public double MinimumStepSize { get; set; } = 1e-2;

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
        public double MinimumScale { get; set; } = System.Math.Sqrt(0.1);

        /// <summary>
        /// Weight of the PDE residual term in the quasi-reversibility recovery
        /// of V from Delta V + aV = 0.
        /// </summary>
        public double VRecoveryResidualWeight { get; set; } = 12.0;

        /// <summary>
        /// Weight of the practical boundary collar condition V = 1 during the
        /// quasi-reversibility recovery of V.
        /// </summary>
        public double VRecoveryDirichletWeight { get; set; } = 3.0;

        /// <summary>
        /// Weight of the practical Neumann collar condition d_n V = 0 during
        /// the quasi-reversibility recovery of V.
        /// </summary>
        public double VRecoveryNeumannWeight { get; set; } = 0.6;

        /// <summary>
        /// H1-style smoothing weight used in the V recovery normal equations.
        /// </summary>
        public double VRecoveryGradientWeight { get; set; } = 8e-3;

        /// <summary>
        /// Small diagonal stabilization in the V-stage normal equations. This is
        /// separate from the mass anchor because it does not bias the solution
        /// towards 1; it only keeps the least-squares system well-conditioned.
        /// </summary>
        public double VRecoveryStabilizationWeight { get; set; } = 5e-5;

        /// <summary>
        /// Small mass penalty anchoring the recovered V field near 1 away from
        /// the boundary so the least-squares stage does not collapse to a floor.
        /// </summary>
        public double VRecoveryMassWeight { get; set; } = 1e-4;

        /// <summary>
        /// When enabled, the service logs per-cycle and per-iteration
        /// convexification diagnostics into the shared workspace/logger stream.
        /// </summary>
        public bool EnableDiagnostics { get; set; } = true;
    }
}
