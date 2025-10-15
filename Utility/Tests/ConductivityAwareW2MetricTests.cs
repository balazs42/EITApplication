using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Reconstruction.ErrorMetrics;
using Xunit;

namespace Utility.Tests
{
    public sealed class ConductivityAwareW2MetricTests
    {
        [Fact]
        public void NormalizationVjpMatchesFiniteDifference()
        {
            double[] raw = { 0.25, -0.4, 0.15, 0.05 };
            double[] g = { 0.3, -0.2, 0.4, 0.1 };
            double kappa = 6.0;
            double epsilon = 1e-6;

            var cache = ConductivityAwareW2Metric.Normalize(raw, kappa, epsilon);
            var vjp = ConductivityAwareW2Metric.ApplyNormalizationVjp(cache, g);

            double h = 1e-6;
            for (int k = 0; k < raw.Length; k++)
            {
                var plus = (double[])raw.Clone();
                plus[k] += h;
                var muPlus = ConductivityAwareW2Metric.Normalize(plus, kappa, epsilon).Normalized;

                var minus = (double[])raw.Clone();
                minus[k] -= h;
                var muMinus = ConductivityAwareW2Metric.Normalize(minus, kappa, epsilon).Normalized;

                double directional = 0.0;
                for (int i = 0; i < g.Length; i++)
                    directional += g[i] * (muPlus[i] - muMinus[i]);
                directional /= (2.0 * h);

                Assert.Equal(directional, vjp[k], 4);
            }
        }

        [Fact]
        public void NormalizeIgnoresNonFiniteEntries()
        {
            double[] raw = { double.NaN, double.PositiveInfinity, -1.0, 0.0 };
            double kappa = 4.0;
            double epsilon = 1e-6;

            var cache = ConductivityAwareW2Metric.Normalize(raw, kappa, epsilon);

            Assert.All(cache.Normalized, v => Assert.True(double.IsFinite(v)));
            Assert.Equal(1.0, cache.Normalized.Sum(), 8);
        }

        [Fact]
        public void OptimalTransportPrimalMatchesMarginals()
        {
            double[,] cost =
            {
                { 0.0, 1.0 },
                { 1.0, 0.0 }
            };
            double[] mu = { 0.6, 0.4 };
            double[] nu = { 0.5, 0.5 };

            var plan = ConductivityAwareW2Metric.SolveOptimalTransportPrimal(cost, mu, nu);

            for (int i = 0; i < mu.Length; i++)
            {
                double row = plan[i, 0] + plan[i, 1];
                Assert.Equal(mu[i], row, 6);
            }

            for (int j = 0; j < nu.Length; j++)
            {
                double col = plan[0, j] + plan[1, j];
                Assert.Equal(nu[j], col, 6);
            }
        }

        [Fact]
        public void SoftGeodesicConvergesToShortestPathForSmallTau()
        {
            var mesh = TinyFEM();
            var metric = new ConductivityAwareW2Metric(new ConductivityAwareW2Metric.Config
            {
                Tau = 1e-3,
                Beta = 1.0,
                Alpha = 1.0,
                UsePhysicsAwareOnly = true
            });

            var geodesics = metric.DebugComputeSoftGeodesics(mesh);
            var mapping = metric.DebugElectrodeNodeMapping(mesh);
            int nodeCount = mesh.ToGraph().Vertices.Count;

            for (int s = 0; s < mapping.Length; s++)
            {
                for (int t = 0; t < mapping.Length; t++)
                {
                    double soft = geodesics.Distances[s, t];
                    double hard = Dijkstra(geodesics.EdgeInfos, nodeCount, mapping[s], mapping[t]);
                    Assert.Equal(hard, soft, 2); // tau → 0 => soft ≈ hard
                }
            }
        }

        [Fact]
        public void GradientMatchesFiniteDifferenceOnTinyMesh()
        {
            var mesh = TinyFEM();
            double[] measured = { 0.1, 0.2, 0.3, 0.4 };
            double[] sigma = { 1.2, 0.8 };
            var config = new ConductivityAwareW2Metric.Config
            {
                Tau = 0.05,
                Alpha = 1.0,
                UsePhysicsAwareOnly = true,
                Beta = 1.0,
                ReturnComponents = true
            };
            var metric = new ConductivityAwareW2Metric(config);

            var simulated = Simulate(mesh, sigma);
            metric.Evaluate(mesh, measured, simulated);
            var adjointSource = metric.EvaluateAdjointSource(mesh, measured, simulated);
            var adjointField = SolveAdjoint(mesh, adjointSource, 0.2);
            var grad = metric.AssembleTotalConductivityGradient(mesh, adjointField);

            var gradCopy = grad.Conductivities.ToDictionary(kv => kv.Key, kv => kv.Value);

            double h = 1e-4;
            var gradFd = new Dictionary<int, double>();
            for (int k = 0; k < sigma.Length; k++)
            {
                var sigmaPlus = (double[])sigma.Clone();
                sigmaPlus[k] += h;
                var simPlus = Simulate(mesh, sigmaPlus);
                double valPlus = metric.Evaluate(mesh, measured, simPlus);

                var sigmaMinus = (double[])sigma.Clone();
                sigmaMinus[k] -= h;
                var simMinus = Simulate(mesh, sigmaMinus);
                double valMinus = metric.Evaluate(mesh, measured, simMinus);

                double finiteDiff = (valPlus - valMinus) / (2.0 * h);
                int elementId = mesh.ElementsTyped[k].Id;
                gradFd[elementId] = finiteDiff;
            }

            // Reset mesh to baseline for sanity
            Simulate(mesh, sigma);
            metric.Evaluate(mesh, measured, simulated);
            adjointSource = metric.EvaluateAdjointSource(mesh, measured, simulated);
            adjointField = SolveAdjoint(mesh, adjointSource, 0.2);
            metric.AssembleTotalConductivityGradient(mesh, adjointField);

            foreach (var kv in gradFd)
            {
                Assert.True(gradCopy.ContainsKey(kv.Key));
                Assert.Equal(kv.Value, gradCopy[kv.Key], 3);
            }
        }

        private static double[] Simulate(FEMMesh mesh, IReadOnlyList<double> sigma)
        {
            var sigmaDict = new Dictionary<int, double>
            {
                [mesh.ElementsTyped[0].Id] = sigma[0],
                [mesh.ElementsTyped[1].Id] = sigma[1]
            };
            mesh.SetConductivityDistribution(new ConductivityDistribution(new Dictionary<int, double>(sigmaDict)));

            var vertices = mesh.GetVertices();
            var potentials = new Dictionary<int, double>
            {
                [vertices[0].GlobalId] = sigma[0],
                [vertices[1].GlobalId] = 0.5 * (sigma[0] + sigma[1]),
                [vertices[2].GlobalId] = sigma[1],
                [vertices[3].GlobalId] = 0.5 * (sigma[0] + sigma[1])
            };
            mesh.SetPotentialDistribution(new PotentialDistribution(potentials));

            return mesh.GetElectrodePotentials();
        }

        private static double Dijkstra(IReadOnlyList<ConductivityAwareW2Metric.EdgeInfo> edges, int nodeCount, int source, int target)
        {
            if (source == target)
                return 0.0;

            var adj = new List<(int to, double cost)>[nodeCount];
            for (int i = 0; i < nodeCount; i++)
                adj[i] = new List<(int, double)>();

            foreach (var e in edges)
            {
                adj[e.U].Add((e.V, e.Cost));
                adj[e.V].Add((e.U, e.Cost));
            }

            var dist = Enumerable.Repeat(double.PositiveInfinity, nodeCount).ToArray();
            var visited = new bool[nodeCount];
            dist[source] = 0.0;

            for (int _ = 0; _ < nodeCount; _++)
            {
                int u = -1;
                double best = double.PositiveInfinity;
                for (int i = 0; i < nodeCount; i++)
                {
                    if (!visited[i] && dist[i] < best)
                    {
                        best = dist[i];
                        u = i;
                    }
                }

                if (u == -1 || u == target)
                    break;

                visited[u] = true;
                foreach (var (to, cost) in adj[u])
                {
                    double candidate = dist[u] + cost;
                    if (candidate < dist[to])
                        dist[to] = candidate;
                }
            }

            return dist[target];
        }

        private static FEMMesh TinyFEM()
        {
            var v1 = new FEMVertex { GlobalId = 0, X = 0, Y = 0, IsElectrode = true };
            var v2 = new FEMVertex { GlobalId = 1, X = 1, Y = 0, IsElectrode = true };
            var v3 = new FEMVertex { GlobalId = 2, X = 1, Y = 1, IsElectrode = true };
            var v4 = new FEMVertex { GlobalId = 3, X = 0, Y = 1, IsElectrode = true };

            var elems = new List<FEMElement>
            {
                new FEMElement(0, v1, v2, v3),
                new FEMElement(1, v1, v3, v4)
            };

            var electrodes = new List<FEMElectrode>
            {
                new FEMElectrode(0, 0, 0.0, 0.0, 0.0, isMeasuring: true),
                new FEMElectrode(1, 1, 0.0, 0.0, 0.0, isMeasuring: true),
                new FEMElectrode(2, 2, 0.0, 0.0, 0.0, isMeasuring: true),
                new FEMElectrode(3, 3, 0.0, 0.0, 0.0, isMeasuring: true)
            };

            return new FEMMesh(new[] { v1, v2, v3, v4 }, elems, electrodes);
        }

        private static PotentialDistribution SolveAdjoint(FEMMesh mesh, double[] electrodeSource, double scale)
        {
            var nodal = ConductivityAwareW2Metric.LiftElectrodeSourceToNodes(mesh, electrodeSource);
            var potentials = new Dictionary<int, double>(mesh.GetVertices().Count);
            foreach (var v in mesh.GetVertices())
            {
                double rhs = nodal.TryGetValue(v.GlobalId, out var value) ? value : 0.0;
                potentials[v.GlobalId] = scale * rhs;
            }

            return new PotentialDistribution(potentials);
        }
    }
}
