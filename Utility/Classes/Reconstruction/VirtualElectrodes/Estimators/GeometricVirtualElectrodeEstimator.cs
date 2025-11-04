using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{
    /// <summary>
    /// Geometric estimator that linearly interpolates values on the circle using nearest
    /// real neighbors in angular order.
    ///
    /// Algorithm:
    /// - Sort real electrodes by angle.
    /// - For each virtual electrode, locate the adjacent arc (left/right) that contains its angle.
    /// - Interpolate linearly between those neighbor measurements proportionally to the arc fraction.
    /// </summary>
    internal class GeometricVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        /// <inheritdoc />
        public virtual double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
            var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
            if (realOrder.Count == 0)
                return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);

            // Interpolate each virtual electrode from its two neighboring real electrodes on the unit circle
            foreach (var electrode in electrodes.Where(e => e.IsVirtual))
            {
                double angle = angles[electrode.Id];
                var (left, right, t) = LocateNeighbors(realOrder, angle);

                // Safe-lookup with fallbacks if one side is missing
                double leftValue = values.TryGetValue(left.Id, out var lv) ? lv : 0.0;
                double rightValue = values.TryGetValue(right.Id, out var rv) ? rv : leftValue;

                // Linear interpolation within the arc fraction
                double interpolated = (1.0 - t) * leftValue + t * rightValue;
                values[electrode.Id] = interpolated;
            }

            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }

        /// <summary>
        /// On a circularly ordered list of real electrodes, finds the arc [Left, Right] that contains
        /// <paramref name="targetAngle"/> and returns the endpoints and the normalized position t in [0,1]
        /// along that arc from Left to Right.
        /// </summary>
        protected static ((int Id, double Angle) Left, (int Id, double Angle) Right, double T) LocateNeighbors(List<(int Id, double Angle)> ordered, double targetAngle)
        {
            if (ordered.Count == 1)
                return (ordered[0], ordered[0], 0.0);

            // Walk circularly over consecutive pairs (i, i+1) and find the arc containing target
            for (int i = 0; i < ordered.Count; i++)
            {
                var current = ordered[i];
                var next = ordered[(i + 1) % ordered.Count];
                double span = VirtualElectrodeHelpers.AngleDelta(current.Angle, next.Angle); // arc length
                double rel = VirtualElectrodeHelpers.AngleDelta(current.Angle, targetAngle); // position from current
                if (rel <= span)
                {
                    double t = span > 0.0 ? rel / span : 0.0;
                    return (current, next, Math.Clamp(t, 0.0, 1.0));
                }
            }

            // Fallback should never happen with normalized angles, but keep it safe
            return (ordered[0], ordered[0], 0.0);
        }
    }
}