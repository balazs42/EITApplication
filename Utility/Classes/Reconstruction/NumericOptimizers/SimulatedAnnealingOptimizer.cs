using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericOptimizers
{
    /// <summary>
    /// Simulated Annealing: random perturbation + acceptance test.
    /// Requires approximate ΔJ ≈ ∇J·Δσ.
    /// </summary>
    public sealed class SimulatedAnnealingOptimizer : INumericOptimizer
    {
        private const double _minConductivity = 1e-6;
        private const double _maxConductivity = 10.0;
        private const double _minTemperature = 1e-6;
        private const double InitialTemperature = 1.0;

        private double _temperature = InitialTemperature;
        private readonly double _cooling = 0.95;
        private readonly Random _rnd = new Random();
        private int _stepsAtMinTemp = 0;
        private const int MaxStepsAtMinTemp = 100;

        public ConductivityDistribution OptimizationStep(ConductivityDistribution sigmaK, ConductivityDistribution totalGradient, double stepSize)
        {
            if (stepSize <= 0.0)
                return sigmaK;

            // Reset temperature if we've been at minimum too long
            if (_temperature <= _minTemperature)
            {
                _stepsAtMinTemp++;
                if (_stepsAtMinTemp >= MaxStepsAtMinTemp)
                {
                    _temperature = InitialTemperature;
                    _stepsAtMinTemp = 0;
                }
            }

            // propose Δσ uniform in [-stepSize,stepSize] with bounds
            var σp = new Dictionary<int, double>();
            foreach (var kv in sigmaK.Conductivities)
            {
                double current = kv.Value;
                double delta = (2 * _rnd.NextDouble() - 1) * stepSize;
                double proposal = current + delta;

                // Clip to valid range
                proposal = Math.Max(_minConductivity, Math.Min(_maxConductivity, proposal));
                proposal = NumericOptimizerGuards.ClipExcessiveGrowth(current, proposal);

                σp[kv.Key] = proposal;
            }

            // approximate ΔJ = ∑g_i Δσ_i with protection against invalid gradients
            double dJ = 0;
            bool validDeltaJ = true;
            foreach (var kv in sigmaK.Conductivities)
            {
                int id = kv.Key;
                double gradient = totalGradient.GetConductivity(id);
                if (!double.IsFinite(gradient))
                {
                    validDeltaJ = false;
                    break;
                }
                dJ += gradient * (σp[id] - kv.Value);
            }

            // accept if downhill or by Metropolis criterion
            bool accept = false;
            if (validDeltaJ)
            {
                if (dJ < 0)
                {
                    accept = true;
                }
                else if (_temperature > _minTemperature)
                {
                    double p = Math.Exp(-dJ / _temperature);
                    accept = _rnd.NextDouble() < p;
                }
            }

            var result = accept ? new ConductivityDistribution(σp) : sigmaK;

            // Update temperature with minimum bound
            _temperature = Math.Max(_minTemperature, _temperature * _cooling);

            // Ensure result stays within bounds
            var bounded = new Dictionary<int, double>();
            foreach (var kv in result.Conductivities)
            {
                bounded[kv.Key] = Math.Max(_minConductivity, Math.Min(_maxConductivity, kv.Value));
            }

            return new ConductivityDistribution(bounded);
        }
    }
}
