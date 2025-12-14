using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{

    /// <summary>
    /// Interpolates virtual electrode values as a convex linear combination of the two geometric neighbors.
    ///
    /// Uses <see cref="VirtualElectrodeSettings.LinearCombinationAlpha"/>:
    /// - If alpha &gt;= 0: a global fixed weight between left/right neighbor.
    /// - If alpha &lt; 0: use geometric t (fraction of arc) from <see cref="GeometricVirtualElectrodeEstimator"/>.
    /// </summary>
    internal sealed class LinearCombinationVirtualElectrodeEstimator : GeometricVirtualElectrodeEstimator
    {
        /// <inheritdoc />
        public override double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
            var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
            if (realOrder.Count == 0)
                return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);

            double alphaGlobal = Math.Clamp(settings.LinearCombinationAlpha, 0.0, 1.0);

            foreach (var electrode in electrodes.Where(e => e.IsVirtual))
            {
                double angle = angles[electrode.Id];
                var (left, right, tGeom) = LocateNeighbors(realOrder, angle);

                // Use provided alpha if non-negative; otherwise use geometric fraction
                double alpha = settings.LinearCombinationAlpha < 0.0 ? tGeom : alphaGlobal;

                double leftValue = values.TryGetValue(left.Id, out var lv) ? lv : 0.0;
                double rightValue = values.TryGetValue(right.Id, out var rv) ? rv : leftValue;

                double interpolated = (1.0 - alpha) * leftValue + alpha * rightValue;
                values[electrode.Id] = interpolated;
            }

            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }
    }
}
