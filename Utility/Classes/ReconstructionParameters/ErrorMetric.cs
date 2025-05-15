namespace Utility.Classes.ReconstructionParameters
{
    public enum ErrorMetric
    {
        L2 = 1,
        Wasserstein2 = 2
    }

    public interface IErrorMetric
    {
        double Evaluate(double[] measured, double[] simulated);
    }

    public sealed class L2Metric : IErrorMetric
    {
        public double Evaluate(double[] measured, double[] simulated)
            => Math.Sqrt(measured.Zip(simulated,
                                      (m, s) => (m - s) * (m - s)).Sum());
    }

    public sealed class Wasserstein2Metric : IErrorMetric
    {
        public double Evaluate(double[] measured, double[] simulated)
        {
            // TODO: real W2 implementation
            return 0.0;
        }
    }
    public static class ErrorMetricFactory
    {
        public static IErrorMetric Create(ErrorMetric choice) => choice switch
        {
            ErrorMetric.L2 => new L2Metric(),
            ErrorMetric.Wasserstein2 => new Wasserstein2Metric(),
            _ => throw new NotSupportedException()
        };
    }
}
