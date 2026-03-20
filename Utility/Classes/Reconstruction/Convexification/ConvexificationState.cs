namespace Utility.Classes.Reconstruction.Convexification
{
    /// <summary>
    /// Aggregates the result of one convexification reconstruction cycle.
    /// The service layer uses this object to publish intermediate frames,
    /// warnings and the final recovered conductivity distribution.
    /// </summary>
    public sealed class ConvexificationState
    {
        public required IReadOnlyList<ConvexificationBoundaryData> BoundaryData { get; init; }
        public required IReadOnlyList<PotentialDistribution> RFields { get; init; }
        public required IReadOnlyList<PotentialDistribution> SFields { get; init; }
        public required IReadOnlyList<PotentialDistribution> WFields { get; init; }
        public required IReadOnlyList<ReconstructionFrame> Frames { get; init; }
        public required ConductivityDistribution ReconstructedConductivity { get; init; }
        public required PotentialDistribution RecoveredCoefficientField { get; init; }
        public required PotentialDistribution RecoveredScaleField { get; init; }
        public required double ObjectiveValue { get; init; }
        public required int IterationCount { get; init; }
        public required bool Converged { get; init; }
        public IReadOnlyList<double> ObjectiveHistory { get; init; } = [];
        public IReadOnlyList<double> AcceptedDampingHistory { get; init; } = [];
        public double RelativeConductivityChange { get; init; }
        public ConductivityDistribution? RawRecoveredConductivity { get; init; }
        public double RawSigmaBelowMinimumFraction { get; init; }
        public double RawSigmaAboveMaximumFraction { get; init; }
        public IReadOnlyList<string> Diagnostics { get; init; } = [];
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }
}
