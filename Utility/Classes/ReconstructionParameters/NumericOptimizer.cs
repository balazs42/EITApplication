namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericOptimizer
    {
        GradientBased = 1,
        PolyakHeavyBall = 2,
        ADAM = 3,
        NesterovAcceleratedGradient = 4,
        GlobalTunnelingDescent = 5,
        HomotopyContinuation = 6,
        SimulatedAnnealing = 7,
        ParticleSwarm = 8
    };

    /// <summary>
    /// Strategy interface for updating conductivity via one optimization step.
    /// </summary>
    public interface INumericOptimizer
    {
        /// <param name="currentSigma">σ⁽ᵏ⁾, current conductivity distribution</param>
        /// <param name="totalGradient">G_σ = ∂J/∂σ, gradient of cost wrt σ</param>
        /// <param name="stepSize">α, base step length</param>
        /// <returns>σ⁽ᵏ⁺¹⁾, updated distribution</returns>
        ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize);
    }

    /// <summary>
    /// Simple gradient descent: σ←σ−α∇J, clamped to physical bounds.
    /// </summary>
    public sealed class GradientBasedOptimizer : INumericOptimizer
    {
        private readonly double _min = 1e-6;
        private readonly double _max = 10.0;
        
        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            // loop over elements
            var next = new Dictionary<int, double>(currentSigma.Conductivities.Count);
            foreach (var kv in currentSigma.Conductivities)
            {
                int id = kv.Key;

                double conductivity = kv.Value;
                double gradient = totalGradient.GetConductivity(id);
                double nextValue = conductivity - stepSize * gradient;          // standard GD step
                
                nextValue = Math.Max(_min, Math.Min(_max, nextValue));          // clamp
                
                next[id] = nextValue;
            }
            return new ConductivityDistribution(next);
        }
    }

    /// <summary>
    /// Polyak Heavy Ball: adds momentum term β*(σ⁽ᵏ⁾−σ⁽ᵏ⁻¹⁾)
    /// Eq: v_{k+1}=βv_k −α∇J, σ_{k+1}=σ_k+v_{k+1}
    /// </summary>
    public sealed class PolyakHeavyBallOptimizer : INumericOptimizer
    {
        private readonly double _beta;
        // store velocity per element
        private Dictionary<int, double>? _velocity;

        public PolyakHeavyBallOptimizer(double beta = 0.8)
        {
            _beta = beta;
            _velocity = null; // lazy init
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            // initialize velocity storage
            if (_velocity == null)
                _velocity = currentSigma.Conductivities.ToDictionary(kv => kv.Key, kv => 0.0);

            var nextVel = new Dictionary<int, double>();
            var nextSigma = new Dictionary<int, double>();

            foreach (var kv in currentSigma.Conductivities)
            {
                int id = kv.Key;
                double conductivity = kv.Value;
                double gradient = totalGradient.GetConductivity(id);
                double v_prev = _velocity[id];

                // v_new = β v_prev − α g
                double v_new = _beta * v_prev - stepSize * gradient;
                // σ_new = σ + v_new
                double nextValue = conductivity + v_new;

                nextVel[id] = v_new;
                nextSigma[id] = nextValue;
            }
            _velocity = nextVel;
            return new ConductivityDistribution(nextSigma);
        }
    }

    /// <summary>
    /// Adam optimizer: adaptive moment estimates.
    /// Maintains per-element m and v, applies bias correction.
    /// </summary>
    public sealed class AdamGradientOptimizer : INumericOptimizer
    {
        private readonly double _beta1 = 0.9;
        private readonly double _beta2 = 0.999;
        private readonly double _eps = 1e-8;
        private int _t = 0;
        private Dictionary<int, double>? _m;  // 1st moment
        private Dictionary<int, double>? _v;  // 2nd moment

        public AdamGradientOptimizer()
        {
            _m = null;
            _v = null;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            // lazy init
            if (_m == null || _v == null)
            {
                _m = currentSigma.Conductivities.ToDictionary(kv => kv.Key, kv => 0.0);
                _v = currentSigma.Conductivities.ToDictionary(kv => kv.Key, kv => 0.0);
            }
            _t++;

            var newSigma = new Dictionary<int, double>();
            foreach (var kv in currentSigma.Conductivities)
            {
                int id = kv.Key;
                double conductivity = kv.Value;
                double gradient = totalGradient.GetConductivity(id);

                // update biased moments
                double m_prev = _m[id];
                double v_prev = _v[id];
                double m_new = _beta1 * m_prev + (1 - _beta1) * gradient;
                double v_new = _beta2 * v_prev + (1 - _beta2) * gradient * gradient;

                _m[id] = m_new;
                _v[id] = v_new;

                // bias-corrected
                double m_hat = m_new / (1 - Math.Pow(_beta1, _t));
                double v_hat = v_new / (1 - Math.Pow(_beta2, _t));

                // update σ
                double σn = conductivity - stepSize * m_hat / (Math.Sqrt(v_hat) + _eps);
                newSigma[id] = σn;
            }
            return new ConductivityDistribution(newSigma);
        }
    }

    /// <summary>
    /// Nesterov accelerated gradient:
    /// y_k = σ_k + γ(σ_k - σ_{k-1}); then σ_{k+1} = y_k - α∇J(y_k)
    /// </summary>
    public sealed class NesterovAcceleratedGradientOptimizer : INumericOptimizer
    {
        private readonly double _gamma;
        private Dictionary<int, double>? _prevSigma;

        public NesterovAcceleratedGradientOptimizer(double gamma = 0.9)
        {
            _gamma = gamma;
            _prevSigma = null;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            // lazy init
            if (_prevSigma == null)
                _prevSigma = currentSigma.Conductivities.ToDictionary(kv => kv.Key, kv => kv.Value);

            // compute y_k = σ_k + γ(σ_k - σ_{k-1})
            var y = new Dictionary<int, double>();
            foreach (var kv in currentSigma.Conductivities)
            {
                int id = kv.Key;
                double conductivityK = kv.Value;
                double conductivityM = _prevSigma[id];
                y[id] = conductivityK + _gamma * (conductivityK - conductivityM);
            }
            // gradient at y -- approximate by using totalGradient at σ_k
            // for full NAG, you'd recompute gradient at y; here we reuse for simplicity
            var nextSigma = new Dictionary<int, double>();
            foreach (var kv in y)
            {
                int id = kv.Key;
                double yv = kv.Value;
                double gradient = totalGradient.GetConductivity(id);
                double nextValue = yv - stepSize * gradient;
                nextSigma[id] = nextValue;
            }

            // push currentSigma to prev
            _prevSigma = currentSigma.Conductivities.ToDictionary(kv => kv.Key, kv => kv.Value);

            return new ConductivityDistribution(nextSigma);
        }
    }

    /// <summary>
    /// Global Tunneling Descent: adds a repeller R around stalled minima.
    /// When gradient norm < tol, step on J+μR to tunnel out.
    /// </summary>
    public sealed class GlobalTunnelingDescentOptimizer : INumericOptimizer
    {
        private readonly double _tol;
        private readonly double _mu0;
        private ConductivityDistribution _anchor;  // σ^k at stall
        private bool _anchored = false;
        private double _mu;
        private const double _delta = 0.5; // width of repeller in log domain

        private GradientBasedOptimizer GradientBasedOptimizer = new();

        public GlobalTunnelingDescentOptimizer(double tolerance = 1e-3, double mu0 = 1.0)
        {
            _tol = tolerance;
            _mu0 = mu0;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution sigmaK, ConductivityDistribution totalGradient, double alpha)
        {
            // compute grad norm
            double normG = Math.Sqrt(sigmaK.Conductivities.Sum(KV => KV.Value * KV.Value));

            if (normG > _tol || !_anchored)
            {
                // normal gradient descent
                _anchored = false;
                return GradientBasedOptimizer.OptimizationStep(sigmaK, totalGradient, alpha);
            }

            // stalled: set anchor and mu
            if (!_anchored)
            {
                _anchor = sigmaK;
                _mu = _mu0;
                _anchored = true;
            }

            // compute repeller gradient in log domain
            var repGrad = new Dictionary<int, double>();
            foreach (var kv in sigmaK.Conductivities)
            {
                int id = kv.Key;
                double conductivity = kv.Value;
                double eta = Math.Log(conductivity);
                double eta0 = Math.Log(_anchor.GetConductivity(id));
                double r = Math.Exp(-Math.Pow(eta - eta0, 2) / (_delta * _delta));
                
                // ∇_σ R = dr/dη * dη/dσ = r * (-2(η-η0)/δ²) * (1/σ)
                double dRdσ = r * (-2 * (eta - eta0) / (_delta * _delta)) / conductivity;
                repGrad[id] = dRdσ;
            }

            // tunnel step: G+μ repGrad
            var total = new Dictionary<int, double>();
            foreach (var kv in totalGradient.Conductivities)
            {
                int id = kv.Key;
                total[id] = kv.Value + _mu * repGrad[id];
            }

            // increase mu for next time
            _mu *= 1.5;
            return GradientBasedOptimizer.OptimizationStep(sigmaK, new ConductivityDistribution(total), alpha);
        }
    }

    /// <summary>
    /// Homotopy Continuation: blends convex prior and true cost via parameter t.
    /// H = (1-t) R₀ + t J, GradH = (1-t) ∇R₀ + t ∇J.  Warm-starts σ each stage.
    /// </summary>
    public sealed class HomotopyContinuationOptimizer : INumericOptimizer
    {
        private readonly ConductivityDistribution _sigmaPrior;
        private readonly int _steps;
        private int _stage;
        private GradientBasedOptimizer GradientBasedOptimizer = new();

        public HomotopyContinuationOptimizer(ConductivityDistribution sigmaPrior, int steps = 20)
        {
            _sigmaPrior = sigmaPrior;
            _steps = steps;
            _stage = 0;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution sigmaK, ConductivityDistribution gradJ, double stepSize)
        {
            // schedule t in [0,1]
            double t = (double)_stage / Math.Max(1, _steps);

            // ∇R₀ = σk - σ_prior
            var gradR0 = sigmaK.Conductivities.ToDictionary(
                kv => kv.Key,
                kv => kv.Value - _sigmaPrior.GetConductivity(kv.Key)
            );

            // GradH = (1-t)*gradR0 + t*gradJ
            var total = new Dictionary<int, double>();
            foreach (var kv in sigmaK.Conductivities)
            {
                int id = kv.Key;
                double g0 = gradR0[id];
                double gJ = gradJ.GetConductivity(id);
                total[id] = (1 - t) * g0 + t * gJ;
            }

            // one GD step on H
            var next = GradientBasedOptimizer.OptimizationStep(sigmaK, new ConductivityDistribution(total), stepSize);
            _stage = Math.Min(_stage + 1, _steps);
            return next;
        }
    }

    /// <summary>
    /// Simulated Annealing: random perturbation + acceptance test.
    /// Requires approximate ΔJ ≈ ∇J·Δσ.
    /// </summary>
    public sealed class SimulatedAnnealingOptimizer : INumericOptimizer
    {
        private double _temperature = 1.0;
        private readonly double _cooling = 0.95;
        private readonly Random _rnd = new Random();

        public ConductivityDistribution OptimizationStep(ConductivityDistribution sigmaK, ConductivityDistribution totalGradient, double stepSize)
        {
            // propose Δσ uniform in [-stepSize,stepSize]
            var σp = new Dictionary<int, double>();
            foreach (var kv in sigmaK.Conductivities)
                σp[kv.Key] = kv.Value + (2 * _rnd.NextDouble() - 1) * stepSize;

            // approximate ΔJ = ∑g_i Δσ_i
            double dJ = 0;
            foreach (var kv in sigmaK.Conductivities)
                dJ += totalGradient.GetConductivity(kv.Key) * (σp[kv.Key] - kv.Value);

            // accept if downhill or by Metropolis criterion
            if (dJ < 0 || _rnd.NextDouble() < Math.Exp(-dJ / _temperature))
                sigmaK = new ConductivityDistribution(σp);

            _temperature *= _cooling;
            return sigmaK;
        }
    }

    /// <summary>
    /// Particle Swarm Optimization stub: requires cost eval to track pbest/gbest.
    /// </summary>
    public sealed class ParticleSwarmOptimizer : INumericOptimizer
    {
        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            // Full PSO requires maintaining a swarm, velocities, personal & global bests,
            // and evaluating cost J(σ). That is beyond a simple one-step interface.
            throw new NotImplementedException(
              "PSO requires swarm state and cost function access.");
        }
    }
}