using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Factories
{
    public static class ErrorMetricFactory
    {
        public static IErrorMetric Create(ErrorMetric choice) => choice switch
        {
            ErrorMetric.L2 => new L2ErrorMetric(),
            ErrorMetric.Wasserstein2 => new Wasserstein2ErrorMetric(),
            _ => throw new NotSupportedException()
        };
    }
}
