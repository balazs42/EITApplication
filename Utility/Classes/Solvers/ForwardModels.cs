using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers
{
    public interface IForwardModel
    {
        public ForwardSolution Solve(Mesh mesh, ConductivityDistribution sigma, BoundaryConditions bc);
    }

    public class FEMForwardModel : IForwardModel
    {
        public ForwardSolution Solve(Mesh mesh, ConductivityDistribution sigma, BoundaryConditions bc)
        {
            // TODO: Assemble stiffness matrix, solve Φ, compute currents …
            return new ForwardSolution();   // placeholder
        }
    }

    public class LbmForwardModel : IForwardModel
    {
        private readonly int _iterations;

        public LbmForwardModel(int iterations = 10_000) => _iterations = iterations;

        public ForwardSolution Solve(
            Mesh mesh,
            ConductivityDistribution sigma,      // not used by scalar LBM yet
            BoundaryConditions bc)
        {
            if (mesh is not LBMMesh grid)
                throw new ArgumentException(
                    "LBM forward model requires an LBMMesh.", nameof(mesh));

            /* ---------------- set up the legacy solver ------------------- */
            LatticeBoltzmannSolver.Configure(grid.Nx, grid.Ny);
            LatticeBoltzmannSolver.Initialize((x, y) => 0.0);   // cold start

            /* ----- electrodes become constant-potential pins ------------- */
            foreach (var e in bc.Electrodes)
            {
                // crude approach: pin *all* vertices of that electrode
                double V = e.Current == 0 ? 0.0 : 1.0;          // demo only!
                foreach (int vid in e.VertexIds)
                {
                    var (x, y) = grid.ToLattice(vid);
                    LatticeBoltzmannSolver.PinNode(x, y, V);
                }
            }

            // optional: obstacles
            // LatticeBoltzmannSolver.AddCentralSquareObstacle();

            /* ---------------- run BGK iterations ------------------------- */
            var field = LatticeBoltzmannSolver.Run(_iterations);

            /* ---------------- collect electrode voltages ----------------- */
            double[] electrodeV = bc.Electrodes
                .Select(e =>
                    e.VertexIds
                     .Select(id =>
                     {
                         var (x, y) = grid.ToLattice(id);
                         return field[x, y];
                     })
                     .Average())
                .ToArray();

            /* ---------------- pack result ------------------------------- */
            double[] nodePot = new double[field.Length];
            for (int y = 0; y < grid.Ny; ++y)
                for (int x = 0; x < grid.Nx; ++x)
                    nodePot[grid.ToVertexId(x, y)] = field[x, y];

            return new ForwardSolution
            {
                SurfaceVoltages = electrodeV,
                NodePotentials = nodePot
            };
        }
    }

    public static class ForwardModelFactory
    {
        public static IForwardModel Create(DifferentialEquationSolver choice) => choice switch
        {
            DifferentialEquationSolver.FiniteElementMethod => new FEMForwardModel(),
            DifferentialEquationSolver.LatticeBoltzmannMethod => new LbmForwardModel(),
            _ => throw new NotSupportedException()
        };
    }
}
