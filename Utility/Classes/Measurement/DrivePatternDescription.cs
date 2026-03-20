using System.Collections.ObjectModel;

namespace Utility.Classes.Measurement
{
    /// <summary>
    /// Compact value object representing a pair of electrodes. The same
    /// structure is used for both the driven excitation pair and individual
    /// measurement pairs. When amplitudes are measured the <see cref="Second"/>
    /// index is equal to <see cref="First"/>; potential-difference sampling
    /// uses distinct indices.
    /// </summary>
    public readonly record struct ElectrodePair(int First, int Second)
    {
        public override string ToString() => $"({First}, {Second})";
    }

    /// <summary>
    /// Describes a single step in a drive pattern: which electrodes are used
    /// for excitation/ground and which pairs are sampled as measurements.
    /// </summary>
    public sealed class MeasurementPatternStep
    {
        public MeasurementPatternStep(ElectrodePair excitation,
                                      IEnumerable<ElectrodePair> measurementPairs,
                                      MeasurementRepresentation representation,
                                      ElectrodeMeasurementSetup measurementSetup)
        {
            Excitation = excitation;
            MeasurementPairs = new ReadOnlyCollection<ElectrodePair>(measurementPairs.ToList());
            Representation = representation;
            MeasurementSetup = measurementSetup;
        }

        /// <summary>The excitation/ground pair used for this step.</summary>
        public ElectrodePair Excitation { get; }

        /// <summary>Electrode pairs that should be sampled in this step.</summary>
        public IReadOnlyList<ElectrodePair> MeasurementPairs { get; }

        /// <summary>Whether values represent amplitudes or differences.</summary>
        public MeasurementRepresentation Representation { get; }

        /// <summary>
        /// Whether measurements on the excitation/ground electrodes are allowed
        /// (<see cref="ElectrodeMeasurementSetup.Active"/>) or explicitly
        /// excluded (<see cref="ElectrodeMeasurementSetup.NonActive"/>).
        /// </summary>
        public ElectrodeMeasurementSetup MeasurementSetup { get; }
    }

    /// <summary>
    /// Full description of a drive pattern expressed as a sequence of
    /// <see cref="MeasurementPatternStep"/> instances. The description makes the
    /// chosen measurement representation and inclusion mode explicit so the
    /// reconstruction pipeline can reason about arbitrary patterns without
    /// guessing.
    /// </summary>
    public sealed class DrivePatternDescription
    {
        public DrivePatternDescription(MeasurementRepresentation representation,
                                       ElectrodeMeasurementSetup measurementSetup,
                                       IReadOnlyList<MeasurementPatternStep> steps)
        {
            Representation = representation;
            MeasurementSetup = measurementSetup;
            Steps = steps ?? throw new ArgumentNullException(nameof(steps));
        }

        /// <summary>Amplitude vs. potential-difference measurement mode.</summary>
        public MeasurementRepresentation Representation { get; }

        /// <summary>Active vs. passive inclusion of driven electrodes.</summary>
        public ElectrodeMeasurementSetup MeasurementSetup { get; }

        /// <summary>Steps in the pattern, one per excitation pair.</summary>
        public IReadOnlyList<MeasurementPatternStep> Steps { get; }

        /// <summary>Total number of distinct excitation steps.</summary>
        public int CycleLength => Steps.Count;

        /// <summary>Returns the step for the specified (possibly wrapped) index.</summary>
        public MeasurementPatternStep GetStep(int index)
        {
            if (Steps.Count == 0)
                throw new InvalidOperationException("Pattern description contains no steps.");

            int normalized = index % Steps.Count;
            if (normalized < 0)
                normalized += Steps.Count;

            return Steps[normalized];
        }
    }
}
