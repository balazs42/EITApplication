using System;
using System.Collections.Concurrent;

namespace Utility.Classes.Measurement
{
    public interface IDrivePatternStrategy
    {
        (int Excitation, int Ground) GetElectrodePair(int electrodeCount, int stepIndex);

        int GetCycleLength(int electrodeCount);
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
        public override (int Excitation, int Ground) GetElectrodePair(int electrodeCount, int stepIndex)
        {
            int cycleLength = GetCycleLength(electrodeCount);
            int normalizedStep = NormalizeStepIndex(stepIndex, cycleLength);
            int excitation = normalizedStep;
            int ground = NormalizeElectrodeIndex(excitation + 1, electrodeCount);
            return (excitation, ground);
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

    public static class DrivePatternStrategyProvider
    {
        private static readonly ConcurrentDictionary<DrivePattern, IDrivePatternStrategy> Strategies = new();

        static DrivePatternStrategyProvider()
        {
            Strategies[DrivePattern.Adjecent] = new AdjacentDrivePatternStrategy();
            Strategies[DrivePattern.Opposite] = new OppositeDrivePatternStrategy();
        }

        public static void RegisterStrategy(DrivePattern pattern, IDrivePatternStrategy strategy, bool overwrite = false)
        {
            if (strategy == null)
                throw new ArgumentNullException(nameof(strategy));

            if (!overwrite && Strategies.ContainsKey(pattern))
                throw new ArgumentException($"A strategy for drive pattern '{pattern}' is already registered.", nameof(pattern));

            Strategies[pattern] = strategy;
        }

        public static IDrivePatternStrategy GetStrategy(DrivePattern pattern)
        {
            if (!Strategies.TryGetValue(pattern, out var strategy))
                throw new NotSupportedException($"No drive pattern strategy registered for '{pattern}'.");

            return strategy;
        }
    }
}
