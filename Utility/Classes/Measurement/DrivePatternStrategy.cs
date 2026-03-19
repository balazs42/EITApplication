using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Utility.Classes.Measurement
{
    public interface IDrivePatternStrategy
    {
        (int Excitation, int Ground) GetElectrodePair(int electrodeCount, int stepIndex);

        int GetCycleLength(int electrodeCount);

        /// <summary>
        /// Builds a full measurement pattern description so callers know exactly
        /// which excitation pair and measurement pairs belong to each step.
        /// </summary>
        DrivePatternDescription BuildDescription(int electrodeCount,
                                                 MeasurementRepresentation representation,
                                                 ElectrodeMeasurementSetup measurementSetup,
                                                 int measurementCount = 0);
    }

    public abstract class DrivePatternStrategyBase : IDrivePatternStrategy
    {
        public virtual int GetCycleLength(int electrodeCount)
        {
            if (electrodeCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(electrodeCount), "Electrode count must be positive.");

            return electrodeCount;
        }

        public abstract (int Excitation, int Ground) GetElectrodePair(int electrodeCount, int stepIndex);

        public virtual DrivePatternDescription BuildDescription(int electrodeCount,
                                                                 MeasurementRepresentation representation,
                                                                 ElectrodeMeasurementSetup measurementSetup,
                                                                 int measurementCount = 0)
        {
            int cycleLength = Math.Max(1, GetCycleLength(electrodeCount));
            var steps = new List<MeasurementPatternStep>(cycleLength);

            for (int i = 0; i < cycleLength; i++)
            {
                var (excitation, ground) = GetElectrodePair(electrodeCount, i);
                var measurementPairs = BuildMeasurementPairs(electrodeCount,
                                                             excitation,
                                                             ground,
                                                             representation,
                                                             measurementSetup,
                                                             measurementCount);

                steps.Add(new MeasurementPatternStep(new ElectrodePair(excitation, ground),
                                                     measurementPairs,
                                                     representation,
                                                     measurementSetup));
            }

            return new DrivePatternDescription(representation, measurementSetup, steps);
        }

        /// <summary>
        /// Generates measurement pairs for the given excitation step. Amplitude
        /// mode samples a single electrode (N–N); potential-difference mode samples
        /// consecutive electrodes while respecting the active/passive setting.
        /// </summary>
        protected virtual IReadOnlyList<ElectrodePair> BuildMeasurementPairs(int electrodeCount,
                                                                            int excitation,
                                                                            int ground,
                                                                            MeasurementRepresentation representation,
                                                                            ElectrodeMeasurementSetup measurementSetup,
                                                                            int measurementCount)
        {
            var pairs = new List<ElectrodePair>();
            if (electrodeCount <= 0)
                return pairs;

            var excluded = new HashSet<int>();
            if (measurementSetup == ElectrodeMeasurementSetup.NonActive)
            {
                excluded.Add(excitation);
                excluded.Add(ground);
            }

            int availableCount = measurementCount > 0 ? measurementCount : electrodeCount;

            if (representation == MeasurementRepresentation.Amplitude)
            {
                for (int i = 0; i < electrodeCount && pairs.Count < availableCount; i++)
                {
                    if (excluded.Contains(i))
                        continue;

                    pairs.Add(new ElectrodePair(i, i));
                }
            }
            else
            {
                for (int i = 0; i < electrodeCount && pairs.Count < availableCount; i++)
                {
                    if (excluded.Contains(i))
                        continue;

                    int next = NormalizeElectrodeIndex(i + 1, electrodeCount);
                    if (measurementSetup == ElectrodeMeasurementSetup.NonActive && excluded.Contains(next))
                        continue;

                    pairs.Add(new ElectrodePair(i, next));
                }
            }

            return pairs;
        }

        protected static int NormalizeStepIndex(int stepIndex, int cycleLength)
        {
            if (cycleLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(cycleLength), "Cycle length must be positive.");

            int normalized = stepIndex % cycleLength;
            return normalized < 0 ? normalized + cycleLength : normalized;
        }

        protected static int NormalizeElectrodeIndex(int index, int electrodeCount)
        {
            if (electrodeCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(electrodeCount), "Electrode count must be positive.");

            int normalized = index % electrodeCount;
            return normalized < 0 ? normalized + electrodeCount : normalized;
        }
    }

    internal sealed class AdjacentDrivePatternStrategy : DrivePatternStrategyBase
    {
        private readonly int _skipCount;

        public AdjacentDrivePatternStrategy(int skipCount = 0)
        {
            _skipCount = Math.Max(0, skipCount);
        }

        public override (int Excitation, int Ground) GetElectrodePair(int electrodeCount, int stepIndex)
        {
            int cycleLength = GetCycleLength(electrodeCount);
            int normalizedStep = NormalizeStepIndex(stepIndex, cycleLength);
            int excitation = normalizedStep;
            int ground = NormalizeElectrodeIndex(excitation + GetOffset(electrodeCount), electrodeCount);
            return (excitation, ground);
        }

        private int GetOffset(int electrodeCount)
        {
            if (electrodeCount <= 1)
                return 0;

            int normalizedSkip = _skipCount % (electrodeCount - 1);
            return normalizedSkip + 1;
        }
    }

    internal sealed class OppositeDrivePatternStrategy : DrivePatternStrategyBase
    {
        public override (int Excitation, int Ground) GetElectrodePair(int electrodeCount, int stepIndex)
        {
            int cycleLength = GetCycleLength(electrodeCount);
            int normalizedStep = NormalizeStepIndex(stepIndex, cycleLength);
            int excitation = normalizedStep;

            int offset = Math.Max(1, electrodeCount / 2);
            int ground = NormalizeElectrodeIndex(excitation + offset, electrodeCount);

            if (ground == excitation)
                ground = NormalizeElectrodeIndex(excitation + 1, electrodeCount);

            return (excitation, ground);
        }
    }

    internal sealed class TrigonometricDrivePatternStrategy : DrivePatternStrategyBase
    {
        public override (int Excitation, int Ground) GetElectrodePair(int electrodeCount, int stepIndex)
        {
            int cycleLength = GetCycleLength(electrodeCount);
            int normalizedStep = NormalizeStepIndex(stepIndex, cycleLength);

            int excitation = normalizedStep;
            int offset = Math.Max(2, (int)Math.Round(electrodeCount / 4.0));
            int ground = NormalizeElectrodeIndex(excitation + offset, electrodeCount);

            if (ground == excitation)
                ground = NormalizeElectrodeIndex(excitation + 1, electrodeCount);

            return (excitation, ground);
        }
    }

    internal sealed class FourierDrivePatternStrategy : DrivePatternStrategyBase
    {
        public override (int Excitation, int Ground) GetElectrodePair(int electrodeCount, int stepIndex)
        {
            int cycleLength = GetCycleLength(electrodeCount);
            int normalizedStep = NormalizeStepIndex(stepIndex, cycleLength);

            int excitation = normalizedStep;
            int harmonic = 1 + (normalizedStep % Math.Max(1, electrodeCount / 2));
            int ground = NormalizeElectrodeIndex(excitation + harmonic, electrodeCount);

            if (ground == excitation)
                ground = NormalizeElectrodeIndex(excitation + 1, electrodeCount);

            return (excitation, ground);
        }
    }

    public static class DrivePatternStrategyProvider
    {
        private static readonly ConcurrentDictionary<DrivePattern, IDrivePatternStrategy> Strategies = new();
        private static readonly ConcurrentDictionary<(DrivePattern Pattern, int SkipCount), IDrivePatternStrategy> ParameterizedStrategies = new();

        static DrivePatternStrategyProvider()
        {
            Strategies[DrivePattern.Adjecent] = new AdjacentDrivePatternStrategy();
            Strategies[DrivePattern.Opposite] = new OppositeDrivePatternStrategy();
            Strategies[DrivePattern.Trigonometric] = new TrigonometricDrivePatternStrategy();
            Strategies[DrivePattern.Fourier] = new FourierDrivePatternStrategy();
            ParameterizedStrategies[(DrivePattern.Adjecent, 0)] = Strategies[DrivePattern.Adjecent];
        }

        public static void RegisterStrategy(DrivePattern pattern, IDrivePatternStrategy strategy, bool overwrite = false)
        {
            if (strategy == null)
                throw new ArgumentNullException(nameof(strategy));

            if (!overwrite && Strategies.ContainsKey(pattern))
                throw new ArgumentException($"A strategy for drive pattern '{pattern}' is already registered.", nameof(pattern));

            Strategies[pattern] = strategy;
            if (pattern == DrivePattern.Adjecent)
                ParameterizedStrategies[(pattern, 0)] = strategy;
        }

        public static IDrivePatternStrategy GetStrategy(DrivePattern pattern)
            => GetStrategy(pattern, 0);

        public static IDrivePatternStrategy GetStrategy(DrivePattern pattern, int skipCount)
        {
            if (pattern == DrivePattern.Adjecent)
            {
                int normalizedSkip = Math.Max(0, skipCount);
                return ParameterizedStrategies.GetOrAdd((pattern, normalizedSkip),
                    key => new AdjacentDrivePatternStrategy(key.SkipCount));
            }

            if (!Strategies.TryGetValue(pattern, out var strategy))
                throw new NotSupportedException($"No drive pattern strategy registered for '{pattern}'.");

            return strategy;
        }
    }
}
