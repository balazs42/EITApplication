using Utility.Classes.Discretizer;
using Utility.Classes.Reconstruction.VirtualElectrodes.Estimators;

namespace Utility.Classes.Reconstruction.VirtualElectrodes
{
    /// <summary>
    /// Encapsulates optional inputs from a forward model (physical model relating conductivity to boundary voltages)
    /// which can help complete virtual electrode potentials.
    ///
    /// None of the properties are strictly required by every estimator; each estimator will
    /// gracefully fall back if a dependency is missing.
    /// </summary>
    public sealed class ForwardModelContext
    {
        /// <summary>
        /// Optional absolute angles (radians, [0, 2π)) for electrode ids. If not provided, angles will be
        /// inferred as equi-spaced around the unit circle in the order of the <see cref="Electrode"/> list.
        /// </summary>
        public IReadOnlyDictionary<int, double>? ElectrodeAngles { get; init; }

        /// <summary>
        /// Optional Jacobian (sensitivity) matrix of the forward model. Rows should correspond to electrode
        /// measurement channels (real then virtual), columns to model parameters (e.g., element conductivities).
        /// Used by <see cref="HarrachVirtualElectrodeEstimator"/>.
        /// </summary>
        public double[,]? Jacobian { get; init; }

        /// <summary>
        /// Number of real (physical) electrode channels present in the Jacobian's top block (rows 0..Real-1).
        /// If not set (&lt;= 0), it will be inferred from the electrode list.
        /// </summary>
        public int RealElectrodeCount { get; init; }

        /// <summary>
        /// Optional per-channel reference voltages to be added back to predicted virtual values in
        /// model-based estimators (e.g., Harrach). Useful if the forward model operates in a reference-free
        /// space and you want to re-introduce the measurement baseline.
        /// </summary>
        public double[]? ReferenceVoltages { get; init; }
    }

    /// <summary>
    /// Helper utilities shared by multiple estimators. Mainly provides
    /// - angle inference/normalization,
    /// - measured voltage mapping by electrode id,
    /// - merging completed values back to ordered arrays,
    /// - circular neighbor search based on angles.
    /// </summary>
    internal static class VirtualElectrodeHelpers
    {
        /// <summary>
        /// Resolves an angle (in radians) for every electrode id.
        /// Preference order:
        /// 1) <see cref="ForwardModelContext.ElectrodeAngles"/> if provided and non-empty.
        /// 2) Equi-spaced angles around the unit circle based on electrode list order.
        /// </summary>
        public static Dictionary<int, double> ResolveAngles(IReadOnlyList<Electrode> electrodes, ForwardModelContext? context)
        {
            if (context?.ElectrodeAngles != null && context.ElectrodeAngles.Count > 0)
                return new Dictionary<int, double>(context.ElectrodeAngles);

            // Default: distribute equi-angularly on [0, 2π)
            var angles = new Dictionary<int, double>(electrodes.Count);
            double step = electrodes.Count > 0 ? 2.0 * Math.PI / electrodes.Count : 0.0;
            for (int i = 0; i < electrodes.Count; i++)
            {
                angles[electrodes[i].Id] = NormalizeAngle(i * step);
            }
            return angles;
        }

        /// <summary>
        /// Returns the subset of angles only for real (non-virtual) electrodes.
        /// </summary>
        public static Dictionary<int, double> ResolveRealAngles(IReadOnlyList<Electrode> electrodes, ForwardModelContext? context)
        {
            var all = ResolveAngles(electrodes, context);
            return electrodes.Where(e => !e.IsVirtual).ToDictionary(e => e.Id, e => all[e.Id]);
        }

        /// <summary>
        /// Wraps an angle into [0, 2π).
        /// </summary>
        public static double NormalizeAngle(double angle)
        {
            double twoPi = Math.PI * 2.0;
            double result = angle % twoPi;
            if (result < 0)
                result += twoPi;
            return result;
        }

        /// <summary>
        /// Computes a forward (counter-clockwise) angular delta from <paramref name="from"/> to <paramref name="to"/>
        /// on the circle, strictly in (0, 2π].
        /// </summary>
        public static double AngleDelta(double from, double to)
        {
            double delta = NormalizeAngle(to - from);
            if (delta <= 0)
                delta += Math.PI * 2.0;
            return delta;
        }

        /// <summary>
        /// Builds a map from real electrode id to measured voltage value, consuming entries from
        /// <paramref name="measuredVoltages"/> in the order real electrodes appear.
        /// Virtual electrodes are skipped.
        /// </summary>
        public static Dictionary<int, double> BuildMeasuredLookup(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages)
        {
            var lookup = new Dictionary<int, double>();
            int idx = 0;
            foreach (var electrode in electrodes)
            {
                if (electrode.IsVirtual)
                    continue; // measured array only contains real electrode channels
                if (idx >= measuredVoltages.Length)
                    break;    // guard: don't overrun measured array
                lookup[electrode.Id] = measuredVoltages[idx++];
            }
            return lookup;
        }

        /// <summary>
        /// Produces a dense array of voltages in the same order as <paramref name="electrodes"/>,
        /// reading values by id from <paramref name="values"/> and defaulting to 0 for missing ids.
        /// </summary>
        public static double[] MergeVoltages(IReadOnlyList<Electrode> electrodes, Dictionary<int, double> values)
        {
            var result = new double[electrodes.Count];
            for (int i = 0; i < electrodes.Count; i++)
            {
                var electrode = electrodes[i];
                result[i] = values.TryGetValue(electrode.Id, out var v) ? v : 0.0;
            }
            return result;
        }

        /// <summary>
        /// Computes angles for all electrodes, then builds an ordered list of real electrodes by angle.
        /// Returns the ordered real list and a lookup of angles for all ids.
        /// </summary>
        public static (List<(int Id, double Angle)> Real, Dictionary<int, double> AllAngles) PrepareOrdering(IReadOnlyList<Electrode> electrodes, ForwardModelContext? context)
        {
            var angleLookup = ResolveAngles(electrodes, context);
            var real = electrodes
                .Where(e => !e.IsVirtual)
                .Select(e => (e.Id, Angle: angleLookup[e.Id]))
                .OrderBy(t => t.Angle)
                .ToList();
            return (real, angleLookup);
        }
    }
}