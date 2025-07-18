using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The error metric factory should be used to create the error metric used in the inverse solve process.
    /// </summary>
    public static class ErrorMetricFactory
    {
        public static IErrorMetric Create(ErrorMetric choice) => choice switch
        {
            ErrorMetric.L2 => CreateL2Metric(),
            ErrorMetric.Wasserstein2 => CreateWasserstein2Metirc(),
            _ => throw new NotSupportedException()
        };


        private static L2ErrorMetric CreateL2Metric()
        {
            return new L2ErrorMetric();
        }

        private static Wasserstein2ErrorMetric CreateWasserstein2Metirc()
        {
            return new Wasserstein2ErrorMetric();
        }
    }
}
