using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Tests
{
    public static class StartupSelfTests
    {
        public static void RunAll()
        {
            var failures = new List<string>();

            Try("L2 metric Test", Test_L2, failures);
            Try("Zero-Order Tikhonov Test", Test_ZeroOrder, failures);
            Try("First-Order Tikhonov (LBM tiny grid) Test", Test_FirstOrder_LBM, failures);
            Try("Laplace (LBM tiny grid) Test", Test_Laplace_LBM, failures);
            Try("Total Variation (LBM tiny grid) Test", Test_TV_LBM, failures);
            Try("Wasserstein-2 (LBM + OR-Tools) Test", Test_W2_LBM, failures, soft: true); // soft: skip OK if OR-Tools not present

            if (failures.Count > 0)
            {
                Debug.WriteLine("Self - tests failed:\n - " + string.Join("\n - ", failures));
               // throw new InvalidOperationException("Self-tests failed:\n - " + string.Join("\n - ", failures));
            }
        }

        private static void Try(string name, Action test, List<string> failures, bool soft = false)
        {
            try { test(); }
            catch (Exception ex)
            {
                if (soft && ex is InvalidOperationException ioe && ioe.Message.Contains("OR-Tools"))
                    return; // allow skipping W2 if solver not available at runtime
                failures.Add($"{name}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static void Test_L2()
        {
            IErrorMetric l2 = new L2ErrorMetric();
            double[] m = { 1, 2, double.NaN, 4 };
            double[] s = { 1.5, 1, 3, double.NaN };
            double J = l2.Evaluate(null!, m, s);
            if (Math.Abs(J - 0.625) > 1e-9) throw new Exception("L2 value mismatch.");
            var adj = l2.EvaluateAdjointSource(null!, m, s);
            var exp = new[] { -0.5, 1.0, 0.0, 0.0 };
            if (!adj.SequenceEqual(exp)) throw new Exception("L2 adjoint mismatch.");
        }

        private static void Test_ZeroOrder()
        {
            var sigma = new ConductivityDistribution(new Dictionary<int, double> { [0] = 2, [1] = 1, [2] = -1 });
            var prior = new ConductivityDistribution(new Dictionary<int, double> { [0] = 1, [1] = 1, [2] = 1 });
            var reg = new ZeroOrderTikhonov(prior, 0.2);

            double val = reg.EvaluateTerm(null!, sigma);
            if (Math.Abs(val - 0.5) > 1e-12) throw new Exception("ZOT value mismatch.");

            var g = reg.EvaluateGradient(null!, sigma);
            if (Math.Abs(g.GetConductivity(0) - 0.2) > 1e-12) throw new Exception("ZOT grad[0]");
            if (Math.Abs(g.GetConductivity(1) - 0.0) > 1e-12) throw new Exception("ZOT grad[1]");
            if (Math.Abs(g.GetConductivity(2) + 0.4) > 1e-12) throw new Exception("ZOT grad[2]");
        }

        private static LBMGrid TinyLBM(int nx = 4, int ny = 4, int measuring = 4)
        {
            var m = new LBMGrid(nx, ny);
            // place a handful of measuring electrodes on non-wall cells
            List<LBMElectrode> electrodes = [];
            int id = 0;
            foreach (var el in m.GetElements().Cast<LBMElement>().Where(e => !e.IsWall))
            {
                if (id >= measuring) break;
                el.IsElectrode = true;
                electrodes.Add(new LBMElectrode(
                    id: id, gridId: el.Id, current: 0, potential: 0, contactImpedance: 0,
                    isExcitation: false, isGround: false, isMeasuring: true));
                id++;
            }
            m.SetElectrodes(electrodes);
            return m;
        }

        private static void Test_FirstOrder_LBM()
        {
            var mesh = TinyLBM();
            var sigma = new ConductivityDistribution(mesh.GetElements().Cast<LBMElement>().ToDictionary(e => e.Id, e => (double)(e.Id % 3)));
            var reg = new FirstOrderTikhonov(0.5);

            double val = reg.EvaluateTerm(mesh, sigma);
            if (!(val >= 0)) throw new Exception("FOT value should be non-negative.");

            var g = reg.EvaluateGradient(mesh, sigma);
            if (g.Conductivities.Count != mesh.GetElements().Count()) throw new Exception("FOT gradient size mismatch.");
        }

        private static void Test_Laplace_LBM()
        {
            var mesh = TinyLBM();
            var sigma = new ConductivityDistribution(mesh.GetElements().Cast<LBMElement>().ToDictionary(e => e.Id, e => (double)((e.Id + 1) % 2)));
            var lap = new LaplaceRegularizer(1.0);

            double val = lap.EvaluateTerm(mesh, sigma);
            if (!(val >= 0)) throw new Exception("Laplace value should be non-negative.");

            var g = lap.EvaluateGradient(mesh, sigma);
            if (g.Conductivities.Count != mesh.GetElements().Count()) throw new Exception("Laplace gradient size mismatch.");
        }

        private static void Test_TV_LBM()
        {
            var mesh = TinyLBM();
            var sigma = new ConductivityDistribution(mesh.GetElements().Cast<LBMElement>().ToDictionary(e => e.Id, e => (double)(e.Id % 2)));
            var tv = new TotalVariationRegularizer(0.1);

            double val = tv.EvaluateTerm(mesh, sigma);
            if (!(val >= 0)) throw new Exception("TV value should be non-negative.");

            var g = tv.EvaluateGradient(mesh, sigma);
            if (g.Conductivities.Count != mesh.GetElements().Count) throw new Exception("TV gradient size mismatch.");
        }

        private static void Test_W2_LBM()
        {
            var mesh = TinyLBM(measuring: 6);

            // Fake two distributions over measuring electrodes (length == mesh.Electrodes.Count)
            var meas = mesh.GetElectrodes().Select((e, i) => (double)(i % 3)).ToArray();
            var sim = mesh.GetElectrodes().Select((e, i) => (double)((i + 1) % 3)).ToArray();

            IErrorMetric w2 = new Wasserstein2ErrorMetric();
            double J = w2.Evaluate(mesh, meas, sim);
            if (!(J >= 0)) throw new Exception("W2 must be non-negative.");

            var phi = w2.EvaluateAdjointSource(mesh, meas, sim);
            if (phi.Length != meas.Length) throw new Exception("W2 adjoint length mismatch.");
        }
    }
}
