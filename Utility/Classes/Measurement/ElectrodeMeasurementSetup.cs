namespace Utility.Classes.Measurement
{
    /// <summary>
    /// Describes whether voltage readings include the actively driven electrodes
    /// or omit them because the instrumentation cannot sample excitation contacts.
    /// </summary>
    public enum ElectrodeMeasurementSetup
    {
        Active,
        Passive
    }
}
