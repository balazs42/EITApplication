using Utility.Classes.Measurement;
using Xunit;

namespace Utility.Tests
{
    public class DrivePatternStrategyTests
    {
        [Fact]
        public void AdjacentStrategy_UsesSkipZeroByDefault()
        {
            var strategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent, 0);

            var pair = strategy.GetElectrodePair(16, 0);

            Assert.Equal(0, pair.Excitation);
            Assert.Equal(1, pair.Ground);
        }

        [Fact]
        public void AdjacentStrategy_SkipThreeMovesGroundByFourElectrodes()
        {
            var strategy = DrivePatternStrategyProvider.GetStrategy(DrivePattern.Adjecent, 3);

            var firstPair = strategy.GetElectrodePair(16, 0);
            var wrappedPair = strategy.GetElectrodePair(16, 13);

            Assert.Equal((0, 4), firstPair);
            Assert.Equal((13, 1), wrappedPair);
        }

        [Fact]
        public void MeasurementPatterns_AdjacentUsesSkipCountInDescription()
        {
            var description = MeasurementPatterns.Adjacent(16,
                                                           MeasurementRepresentation.Amplitude,
                                                           ElectrodeMeasurementSetup.NonActive,
                                                           drivePatternSkip: 3);

            var firstStep = description.GetStep(0);

            Assert.Equal(0, firstStep.Excitation.First);
            Assert.Equal(4, firstStep.Excitation.Second);
        }
    }
}
