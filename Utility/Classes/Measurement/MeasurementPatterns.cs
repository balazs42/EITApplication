namespace Utility.Classes.Measurement
{
    /// <summary>
    /// Provides convenient factory helpers for common electrode drive patterns.
    /// Each method delegates to the registered <see cref="IDrivePatternStrategy"/>
    /// so that callers receive a fully specified <see cref="DrivePatternDescription"/>.
    /// </summary>
    public static class MeasurementPatterns
    {
        public static DrivePatternDescription Adjacent(int electrodeCount,
                                                       MeasurementRepresentation representation,
                                                       ElectrodeMeasurementSetup measurementSetup,
                                                       int measurementCount = 0,
                                                       int drivePatternSkip = 0)
            => Build(DrivePattern.Adjecent, electrodeCount, representation, measurementSetup, measurementCount, drivePatternSkip);

        public static DrivePatternDescription Opposite(int electrodeCount,
                                                       MeasurementRepresentation representation,
                                                       ElectrodeMeasurementSetup measurementSetup,
                                                       int measurementCount = 0)
            => Build(DrivePattern.Opposite, electrodeCount, representation, measurementSetup, measurementCount);

        public static DrivePatternDescription Trigonometric(int electrodeCount,
                                                             MeasurementRepresentation representation,
                                                             ElectrodeMeasurementSetup measurementSetup,
                                                             int measurementCount = 0)
            => Build(DrivePattern.Trigonometric, electrodeCount, representation, measurementSetup, measurementCount);

        public static DrivePatternDescription Fourier(int electrodeCount,
                                                      MeasurementRepresentation representation,
                                                      ElectrodeMeasurementSetup measurementSetup,
                                                      int measurementCount = 0)
            => Build(DrivePattern.Fourier, electrodeCount, representation, measurementSetup, measurementCount);

        /// <summary>
        /// Generic entry point that leverages the configured drive-pattern
        /// strategies. Custom patterns can be registered through
        /// <see cref="DrivePatternStrategyProvider.RegisterStrategy"/> and will be
        /// reachable via this helper.
        /// </summary>
        public static DrivePatternDescription Build(DrivePattern pattern,
                                                    int electrodeCount,
                                                    MeasurementRepresentation representation,
                                                    ElectrodeMeasurementSetup measurementSetup,
                                                    int measurementCount = 0,
                                                    int drivePatternSkip = 0)
        {
            var strategy = DrivePatternStrategyProvider.GetStrategy(pattern, drivePatternSkip);
            return strategy.BuildDescription(electrodeCount, representation, measurementSetup, measurementCount);
        }
    }
}
