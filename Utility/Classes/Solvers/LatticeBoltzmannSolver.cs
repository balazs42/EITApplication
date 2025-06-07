using Utility.Classes.Meshing;
using MathNet.Numerics.LinearAlgebra;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// The core "engine" for all LBM-based calculations.
    /// This class is a proper object with state managed internally.
    /// </summary>
    public sealed class LatticeBoltzmannSolver
    {
        private readonly (int cx, int cy)[] _c = { (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1) };
        private readonly int[] _opp = { 0, 3, 4, 1, 2, 7, 8, 5, 6 };
        private readonly double[] _w = { 4.0 / 9, 1.0 / 9, 1.0 / 9, 1.0 / 9, 1.0 / 9, 1.0 / 36, 1.0 / 36, 1.0 / 36, 1.0 / 36 };

        public PotentialDistribution RunSimulation(LBMMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc, int iterations, Vector<double> source = null)
        {
            Initialize(mesh, sigma);
            ApplyBoundaryConditions(mesh, bc);
            var sourceField = MapSourceToGrid(mesh, source);

            for (int t = 0; t < iterations; ++t)
                Step(mesh, sourceField);

            return PackResult(mesh);
        }

        public ConductivityDistribution ComputeGradient(LBMMesh mesh, PotentialDistribution phi, PotentialDistribution mu)
        {
            var gradPhi = LatticeBoltzmannOperators.CalculateGradient(mesh, phi);
            var gradMu = LatticeBoltzmannOperators.CalculateGradient(mesh, mu);
            var gradientDict = new Dictionary<int, double>();
            foreach (var vertex in mesh.Vertices)
            {
                var gP = gradPhi.GetVector(vertex.GlobalId);
                var gM = gradMu.GetVector(vertex.GlobalId);
                gradientDict[vertex.GlobalId] = gP.X * gM.X + gP.Y * gM.Y;
            }
            return new ConductivityDistribution(gradientDict);
        }

        #region Private LBM Helpers
        private void Initialize(LBMMesh mesh, ConductivityDistribution sigma)
        {
            foreach (var element in mesh.Elements.Cast<LBMElement>())
            {
                element.Conductivity = sigma.GetConductivity(element.Id);
                element.IsPinned = false;
                for (int k = 0; k < 9; k++)
                    element.Fi[k] = _w[k] * 0.0;
            }
        }

        private void ApplyBoundaryConditions(LBMMesh mesh, BoundaryConditions bc)
        {
            if (bc?.Electrodes == null) return;
            foreach (var electrode in bc.Electrodes)
            {
                bool isSource = electrode.Current > 0;
                foreach (int vertexId in electrode.VertexIds)
                {
                    var (x, y) = mesh.ToLattice(vertexId);
                    if (x >= 0 && x < mesh.Nx && y >= 0 && y < mesh.Ny)
                    {
                        var element = mesh.GetElementAt(x, y);
                        element.IsPinned = true;
                        element.PinValue = isSource ? 1.0 : -1.0;
                    }
                }
            }
        }

        private void Step(LBMMesh mesh, double[,] sourceField)
        {
            var gNext = new double[mesh.Nx, mesh.Ny, 9];

            for (int y = 0; y < mesh.Ny; y++)
            {
                for (int x = 0; x < mesh.Nx; x++)
                {
                    var element = mesh.GetElementAt(x, y);
                    if (element.IsWall) continue;
                    double localPhi = element.Fi.Sum();
                    double tau = (element.Conductivity / (1.0 / 3.0)) + 0.5;
                    double omega = 1.0 / tau;
                    double source = (sourceField != null) ? sourceField[x, y] : 0.0;

                    for (int k = 0; k < 9; k++)
                    {
                        double geq = _w[k] * localPhi;
                        element.Fi[k] += -omega * (element.Fi[k] - geq) + _w[k] * source;
                    }
                }
            }

            for (int y = 0; y < mesh.Ny; y++)
            {
                for (int x = 0; x < mesh.Nx; x++)
                {
                    var element = mesh.GetElementAt(x, y);
                    if (element.IsWall) continue;
                    for (int k = 0; k < 9; k++)
                    {
                        int xn = x + _c[k].cx;
                        int yn = y + _c[k].cy;
                        if (xn >= 0 && xn < mesh.Nx && yn >= 0 && yn < mesh.Ny && !mesh.GetElementAt(xn, yn).IsWall)
                            gNext[xn, yn, k] = element.Fi[k];
                        else
                            gNext[x, y, _opp[k]] = element.Fi[k];
                    }
                }
            }

            for (int y = 0; y < mesh.Ny; y++)
            {
                for (int x = 0; x < mesh.Nx; x++)
                {
                    var element = mesh.GetElementAt(x, y);
                    if (element.IsWall)
                        continue;
                    for (int k = 0; k < 9; ++k)
                        element.Fi[k] = gNext[x, y, k];

                    if (element.IsPinned)
                        for (int k = 0; k < 9; k++)
                            element.Fi[k] = _w[k] * element.PinValue;
                }
            }
        }

        private double[,] MapSourceToGrid(LBMMesh mesh, Vector<double> source)
        {
            if (source == null) 
                return null;

            var sourceField = new double[mesh.Nx, mesh.Ny];
            for (int i = 0; i < source.Count; ++i)
            {
                var (x, y) = mesh.ToLattice(i);
                if (x >= 0 && x < mesh.Nx && y >= 0 && y < mesh.Ny)
                    sourceField[x, y] = source[i];
            }

            return sourceField;
        }

        private PotentialDistribution PackResult(LBMMesh lbmMesh)
        {
            var potentials = new Dictionary<int, double>();
            for (int y = 0; y < lbmMesh.Ny; ++y)
            {
                for (int x = 0; x < lbmMesh.Nx; ++x)
                {
                    var element = lbmMesh.GetElementAt(x, y);
                    potentials[element.Id] = element.Fi.Sum();
                }
            }
            return new PotentialDistribution(potentials);
        }
        #endregion
    }
}