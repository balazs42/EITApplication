using System.Collections.Generic;

namespace Utility.Classes.Measurement
{
    /// <summary>
    /// Aggregates all information required to process a single drive-pattern step:
    /// - The raw and prepared measurement frames
    /// - The measurement pattern used to map channels
    /// - The underlying pattern description and concrete step (excitation/measurement pairs)
    /// - The normalised step index so callers can align excitation, measurement and boundary conditions
    /// </summary>
    public sealed class MeasurementStepContext
    {
        public MeasurementStepContext(int requestedStepIndex,
                                      int normalizedStepIndex,
                                      double[] rawFrame,
                                      double[] preparedFrame,
                                      MeasurementPattern pattern,
                                      DrivePatternDescription? patternDescription,
                                      MeasurementPatternStep? step)
        {
            RequestedStepIndex = requestedStepIndex;
            NormalizedStepIndex = normalizedStepIndex;
            RawFrame = rawFrame;
            PreparedFrame = preparedFrame;
            Pattern = pattern;
            PatternDescription = patternDescription;
            Step = step;
        }

        /// <summary>The step index requested by the caller (may be outside the cycle length).</summary>
        public int RequestedStepIndex { get; }

        /// <summary>Step index wrapped to the pattern cycle length.</summary>
        public int NormalizedStepIndex { get; }

        /// <summary>The raw measurement frame before sanitisation or padding.</summary>
        public double[] RawFrame { get; }

        /// <summary>Measurement frame mapped to solver ordering with NaN padding applied.</summary>
        public double[] PreparedFrame { get; }

        /// <summary>Pattern used to map measurement channels for the step.</summary>
        public MeasurementPattern Pattern { get; }

        /// <summary>Full drive/measurement pattern description if one is available.</summary>
        public DrivePatternDescription? PatternDescription { get; }

        /// <summary>Concrete step within the drive/measurement cycle (excitation and measurement pairs).</summary>
        public MeasurementPatternStep? Step { get; }
    }
}
