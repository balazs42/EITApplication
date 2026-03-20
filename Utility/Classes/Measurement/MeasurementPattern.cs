using System.Collections.ObjectModel;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;

namespace Utility.Classes.Measurement
{
    /// <summary>
    /// Describes how a single measurement frame should be interpreted by the
    /// reconstruction pipeline.  A pattern captures the relationship between
    /// raw measurement entries and electrode indices and enables the solver to
    /// remap values for the four supported acquisition modes:
    /// <list type="number">
    /// <item><description>
    /// Option 1 – active electrodes, absolute amplitudes: every electrode
    /// contributes a single entry that maps directly onto its potential.
    /// </description></item>
    /// <item><description>
    /// Option 2 – active electrodes, potential differences: the frame stores
    /// consecutive differences V_i − V_{i+1} (including the wrap-around term).
    /// </description></item>
    /// <item><description>
    /// Option 3 – non-active electrodes, potential differences: only the
    /// differences that do not touch the driven electrodes remain and the
    /// absent pairs are represented as NaNs during sanitisation.
    /// </description></item>
    /// <item><description>
    /// Option 4 – non-active electrodes, amplitudes: only measuring electrodes
    /// provide values and driven contacts are ignored via NaN placeholders.
    /// </description></item>
    /// </list>
    /// </summary>
    public sealed class MeasurementPattern
    {
        private readonly ReadOnlyCollection<MeasurementChannel> _channels;
        private readonly Dictionary<int, MeasurementChannel> _channelByTargetIndex;

        internal MeasurementPattern(MeasurementRepresentation representation,
                                    ElectrodeMeasurementSetup measurementSetup,
                                    int sanitizedLength,
                                    IList<MeasurementChannel> channels)
        {
            Representation = representation;
            MeasurementSetup = measurementSetup;
            SanitizedLength = sanitizedLength;
            _channels = new ReadOnlyCollection<MeasurementChannel>(channels.ToList());
            _channelByTargetIndex = channels.ToDictionary(c => c.TargetIndex);
        }

        /// <summary>
        /// Describes whether the pattern represents direct amplitudes or
        /// potential differences.
        /// </summary>
        public MeasurementRepresentation Representation { get; }

        /// <summary>
        /// Identifies whether the underlying acquisition provided data on all
        /// electrodes (<see cref="ElectrodeMeasurementSetup.Active"/>) or
        /// omitted the currently driven contacts
        /// (<see cref="ElectrodeMeasurementSetup.NonActive"/>).
        /// </summary>
        public ElectrodeMeasurementSetup MeasurementSetup { get; }

        /// <summary>
        /// The length of the sanitised vectors forwarded to the error metrics
        /// (always equal to the electrode count for the associated mesh).
        /// </summary>
        public int SanitizedLength { get; }

        /// <summary>Channels that are populated by the provided measurements.</summary>
        public IReadOnlyList<MeasurementChannel> Channels => _channels;

        internal bool TryGetChannel(int targetIndex, out MeasurementChannel channel)
            => _channelByTargetIndex.TryGetValue(targetIndex, out channel);

        /// <summary>
        /// Converts raw measurement frames into solver-aligned vectors.  The
        /// caller supplies either a short frame that contains only the active
        /// channels (Options 3 &amp; 4) or a full frame (Options 1 &amp; 2).  Missing
        /// entries are padded with NaNs so that residuals ignore them.
        /// </summary>
        public double[] MapMeasurement(double[] measurement)
        {
            double[] sanitized = Enumerable.Repeat(double.NaN, SanitizedLength).ToArray();
            if (measurement.Length == SanitizedLength)
            {
                Array.Copy(measurement, sanitized, SanitizedLength);
                return sanitized;
            }

            int cursor = 0;
            foreach (var channel in _channels)
            {
                if (cursor >= measurement.Length)
                {
                    Workspace.AddWarningMessage(
                        $"Measurement frame missing entries for target index {channel.TargetIndex}. Remaining slots will be ignored.");
                    break;
                }

                sanitized[channel.TargetIndex] = measurement[cursor++];
            }

            if (measurement.Length > _channels.Count)
            {
                Workspace.AddWarningMessage(
                    $"Discarding {measurement.Length - _channels.Count} surplus measurement values that do not map to the current pattern.");
            }

            return sanitized;
        }

        /// <summary>
        /// Projects simulated electrode potentials into the representation
        /// dictated by the pattern.  Missing channels (e.g. driven electrodes in
        /// Option 3 and Option 4) are padded with NaN so error metrics skip them.
        /// </summary>
        public double[] ProjectSimulated(double[] potentials)
        {
            if (potentials.Length != SanitizedLength)
                throw new ArgumentException("Simulated potential vector length does not match the pattern.", nameof(potentials));

            double[] projected = Enumerable.Repeat(double.NaN, SanitizedLength).ToArray();

            if (Representation == MeasurementRepresentation.Amplitude)
            {
                foreach (var channel in _channels)
                {
                    int idx = channel.FirstElectrodeIndex;
                    if (idx < 0 || idx >= potentials.Length)
                        continue;

                    projected[channel.TargetIndex] = potentials[idx];
                }
            }
            else
            {
                foreach (var channel in _channels)
                {
                    int left = channel.FirstElectrodeIndex;
                    int right = channel.SecondElectrodeIndex;
                    if (left < 0 || left >= potentials.Length)
                        continue;
                    if (right < 0 || right >= potentials.Length)
                        continue;

                    double a = potentials[left];
                    double b = potentials[right];
                    projected[channel.TargetIndex] = double.IsNaN(a) || double.IsNaN(b) ? double.NaN : a - b;
                }
            }

            return projected;
        }

        /// <summary>
        /// Extracts a raw measurement frame (without NaN padding) from a
        /// simulated potential vector.  This mirrors what the instrumentation
        /// would deliver for each of the four options.
        /// </summary>
        public double[] ExtractRawMeasurement(double[] potentials)
        {
            double[] frame = new double[_channels.Count];
            for (int i = 0; i < _channels.Count; i++)
            {
                var channel = _channels[i];
                if (Representation == MeasurementRepresentation.Amplitude)
                {
                    frame[i] = potentials[channel.FirstElectrodeIndex];
                }
                else
                {
                    double left = potentials[channel.FirstElectrodeIndex];
                    double right = potentials[channel.SecondElectrodeIndex];
                    frame[i] = double.IsNaN(left) || double.IsNaN(right) ? double.NaN : left - right;
                }
            }

            return frame;
        }
    }

    /// <summary>
    /// A single measurement channel mapping a raw frame entry to a concrete
    /// electrode index (amplitude mode) or electrode pair (difference mode).
    /// </summary>
    public sealed record MeasurementChannel(int TargetIndex, int FirstElectrodeIndex, int SecondElectrodeIndex);

    public enum MeasurementRepresentation
    {
        Amplitude,
        PotentialDifference
    }

    public static class MeasurementPatternBuilder
    {
        public static MeasurementPattern Build(IList<Electrode> electrodes,
                                               ElectrodeMeasurementSetup setup,
                                               bool usePotentialDifferences)
        {
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));

            int electrodeCount = electrodes.Count;
            if (electrodeCount == 0)
                return new MeasurementPattern(MeasurementRepresentation.Amplitude,
                                              ElectrodeMeasurementSetup.Active,
                                              0,
                                              []);

            if (!usePotentialDifferences)
                return BuildAmplitudePattern(electrodes, setup);

            return BuildDifferencePattern(electrodes, setup);
        }

        public static MeasurementPattern BuildFromStep(IList<Electrode> electrodes,
                                                       MeasurementPatternStep step)
        {
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));
            if (step == null)
                throw new ArgumentNullException(nameof(step));

            int electrodeCount = electrodes.Count;
            var channels = new List<MeasurementChannel>(step.MeasurementPairs.Count);

            foreach (var pair in step.MeasurementPairs)
            {
                int targetIndex = pair.First;
                channels.Add(new MeasurementChannel(targetIndex, pair.First, pair.Second));
            }

            return new MeasurementPattern(step.Representation,
                                          step.MeasurementSetup,
                                          electrodeCount,
                                          channels);
        }

        private static MeasurementPattern BuildAmplitudePattern(IList<Electrode> electrodes,
                                                                ElectrodeMeasurementSetup setup)
        {
            int electrodeCount = electrodes.Count;
            var channels = new List<MeasurementChannel>(electrodeCount);

            for (int i = 0; i < electrodeCount; i++)
            {
                bool include = setup == ElectrodeMeasurementSetup.Active || electrodes[i].IsMeasuring;
                if (!include)
                    continue;

                channels.Add(new MeasurementChannel(i, i, i));
            }

            if (channels.Count == 0)
                Workspace.AddWarningMessage("Amplitude measurement pattern contains no measuring electrodes.");

            return new MeasurementPattern(MeasurementRepresentation.Amplitude,
                                          setup,
                                          electrodeCount,
                                          channels);
        }

        private static MeasurementPattern BuildDifferencePattern(IList<Electrode> electrodes,
                                                                 ElectrodeMeasurementSetup setup)
        {
            int electrodeCount = electrodes.Count;
            var channels = new List<MeasurementChannel>(electrodeCount);
            var skipped = new HashSet<int>();

            if (setup == ElectrodeMeasurementSetup.Passive)
            {
                for (int i = 0; i < electrodeCount; i++)
                {
                    if (electrodes[i].IsMeasuring)
                        continue;

                    skipped.Add(i);
                    skipped.Add((i - 1 + electrodeCount) % electrodeCount);
                }
            }

            for (int i = 0; i < electrodeCount; i++)
            {
                if (skipped.Contains(i))
                    continue;

                int next = (i + 1) % electrodeCount;
                channels.Add(new MeasurementChannel(i, i, next));
            }

            if (channels.Count == 0)
                Workspace.AddWarningMessage("Potential-difference measurement pattern contains no valid channels.");

            return new MeasurementPattern(MeasurementRepresentation.PotentialDifference,
                                          setup,
                                          electrodeCount,
                                          channels);
        }
    }

    /// <summary>
    /// Bundles the sanitised measurement vectors and the pattern that produced
    /// them.  The projection handles the adjoint back-projection so that error
    /// metrics remain agnostic of the acquisition mode.
    /// </summary>
    public sealed class MeasurementProjection
    {
        private readonly MeasurementPattern _pattern;

        internal MeasurementProjection(MeasurementPattern pattern, double[] measured, double[] simulated)
        {
            _pattern = pattern;
            Measured = measured;
            Simulated = simulated;
        }

        public MeasurementPattern Pattern => _pattern;
        public double[] Measured { get; }
        public double[] Simulated { get; }

        /// <summary>
        /// Expands an adjoint vector defined in measurement space back onto the
        /// per-electrode representation expected by the solvers.
        /// </summary>
        public double[] ExpandAdjoint(double[] adjoint)
        {
            double[] expanded = new double[_pattern.SanitizedLength];

            if (_pattern.Representation == MeasurementRepresentation.Amplitude)
            {
                foreach (var channel in _pattern.Channels)
                {
                    if (channel.TargetIndex < 0 || channel.TargetIndex >= adjoint.Length)
                        continue;

                    expanded[channel.FirstElectrodeIndex] = adjoint[channel.TargetIndex];
                }
            }
            // Potential differences branch
            else
            {
                foreach (var channel in _pattern.Channels)
                {
                    if (channel.TargetIndex < 0 || channel.TargetIndex >= adjoint.Length)
                        continue;

                    double value = adjoint[channel.TargetIndex];
                    if (!double.IsFinite(value))
                        continue;

                    // Adding and subtracting ensures proper accumulation
                    expanded[channel.FirstElectrodeIndex] += value;
                    expanded[channel.SecondElectrodeIndex] -= value;
                }
            }

            double sum = expanded.Sum();
            // Ensure zero mean adjustment to avoid global potential shifts
            if (sum > 1e-6 && sum < 1e6)
            {
                double mean = expanded.Average();

                for(int i = 0; i < expanded.Length; i++)
                    expanded[i] -= mean;
            }

            return expanded;
        }
    }

    /// <summary>
    /// Convenience factory that produces measurement projections for measured
    /// and simulated frames.  The factory ensures both vectors share the same
    /// sanitised layout before the error metrics are invoked.
    /// </summary>
    public static class MeasurementProjector
    {
        public static MeasurementProjection Create(IList<Electrode> electrodes,
                                                   ElectrodeMeasurementSetup setup,
                                                   bool usePotentialDifferences,
                                                   double[] measurement,
                                                   double[] simulated)
        {
            var pattern = MeasurementPatternBuilder.Build(electrodes, setup, usePotentialDifferences);

            double[] measured;
            if (measurement.Length == pattern.SanitizedLength)
            {
                // Option 2 (active electrodes, potential differences) already supplies
                // one value per electrode pair in solver order, so we simply retain the
                // provided frame.  Cloning avoids mutating the caller-owned buffer.
                measured = (double[])measurement.Clone();
            }
            else
            {
                // Options 3 & 4 deliver compact frames that require NaN padding so
                // error metrics skip the driven electrodes.  MapMeasurement() handles
                // the expansion without re-applying difference calculations.
                measured = pattern.MapMeasurement(measurement);
            }
            var projectedSimulated = pattern.ProjectSimulated(simulated);
            return new MeasurementProjection(pattern, measured, projectedSimulated);
        }
    }
}
