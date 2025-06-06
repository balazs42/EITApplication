using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// The core "engine" for all FEM-based calculations.
    /// This class contains the detailed logic for matrix assembly and solving.
    /// It does NOT implement the IDifferentialEquationSolver interface directly.
    /// </summary>
    public sealed class FiniteElementSolver
    {
        private readonly INumericSolver _numericSolver;

        public FiniteElementSolver(INumericSolver numericSolver)
        {
            _numericSolver = numericSolver;
        }

        /// <summary>
        /// The main engine method. Solves a general FEM system for the Complete Electrode Model.
        /// </summary>
        public PotentialDistribution SolveSystem(IMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc, Vector<double> potentialSourceTerm = null)
        {
            if (mesh is not FEMMesh femMesh)
                throw new ArgumentException("FiniteElementSolver requires an FEMMesh.", nameof(mesh));

            int numVertices = femMesh.Vertices.Count;
            int numElectrodes = bc.Electrodes.Count;
            int groundElectrodeId = numElectrodes - 1; // Convention: ground the last electrode

            var K = BuildStiffnessMatrix(femMesh, sigma);
            var M = BuildRobinMassMatrix(femMesh, bc);
            var A = BuildCouplingMatrix(femMesh, bc);
            var D = BuildElectrodeMatrix(femMesh, bc);
            var S = BuildSystemMatrix(K, M, A, D, numVertices, numElectrodes);
            var F = BuildRhsVector(bc, numVertices, potentialSourceTerm);

            // Apply pinned node values before grounding
            var pinnedNodes = FindPinnedNodes(femMesh);
            ApplyDirichletConditions(S, F, pinnedNodes);

            var (S_grounded, F_grounded) = ApplyGrounding(S, F, numVertices, groundElectrodeId);
            var solution_grounded = _numericSolver.SolveLinearSystem(S_grounded.ToArray(), F_grounded.ToArray());
            var fullSolution = ReconstructFullSolution(solution_grounded, numVertices, numElectrodes, groundElectrodeId);

            var nodalPotentials = fullSolution.SubVector(0, numVertices);
            var potentialDict = new Dictionary<int, double>();
            for (int i = 0; i < numVertices; i++)
                potentialDict[femMesh.Vertices[i].GlobalId] = nodalPotentials[i];

            return new PotentialDistribution(potentialDict);
        }

        public ConductivityDistribution ComputeGradient(IMesh mesh, PotentialDistribution phi, PotentialDistribution mu)
        {
            if (mesh is not FEMMesh femMesh)
                throw new ArgumentException("FEM gradient computation requires a FEMMesh.");

            var gradPhi = FiniteElementOperators.CalculateElementWiseGradient(femMesh, phi);
            var gradMu = FiniteElementOperators.CalculateElementWiseGradient(femMesh, mu);
            var gradientDict = new Dictionary<int, double>();
            foreach (var element in femMesh.Elements)
            {
                var gP = gradPhi.GetVector(element.Id);
                var gM = gradMu.GetVector(element.Id);
                gradientDict[element.Id] = gP.X * gM.X + gP.Y * gM.Y;
            }
            return new ConductivityDistribution(gradientDict);
        }

        #region Private Helpers

        private Matrix<double> BuildStiffnessMatrix(FEMMesh mesh, ConductivityDistribution sigma)
        {
            var K = Matrix<double>.Build.Sparse(mesh.Vertices.Count, mesh.Vertices.Count);
            foreach (var element in mesh.Elements)
            {
                double conductivity = sigma.GetConductivity(element.Id);
                var v = element.Vertices;

                var gradN = new (double dx, double dy)[3];
                gradN[0] = ((v[1].Y - v[2].Y), (v[2].X - v[1].X));
                gradN[1] = ((v[2].Y - v[0].Y), (v[0].X - v[2].X));
                gradN[2] = ((v[0].Y - v[1].Y), (v[1].X - v[0].X));

                var kLocal = Matrix<double>.Build.Dense(3, 3);
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        double dotProduct = gradN[i].dx * gradN[j].dx + gradN[i].dy * gradN[j].dy;
                        kLocal[i, j] = conductivity * dotProduct / (4 * element.Area);
                    }
                }

                int[] gids = { v[0].GlobalId, v[1].GlobalId, v[2].GlobalId };
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        K[gids[i], gids[j]] += kLocal[i, j];
            }
            return K;
        }

        private Matrix<double> BuildRobinMassMatrix(FEMMesh mesh, BoundaryConditions bc)
        {
            var M = Matrix<double>.Build.Sparse(mesh.Vertices.Count, mesh.Vertices.Count);
            foreach (var electrode in bc.Electrodes)
            {
                if (Math.Abs(electrode.ZContact) < 1e-12) continue;
                var electrodeEdges = FindElectrodeEdges(mesh, electrode);
                foreach (var edge in electrodeEdges)
                {
                    double length = Math.Sqrt(Math.Pow(edge.End.X - edge.Start.X, 2) + Math.Pow(edge.End.Y - edge.Start.Y, 2));
                    int u = edge.Start.GlobalId;
                    int v = edge.End.GlobalId;
                    double c = length / (6.0 * electrode.ZContact);
                    M[u, u] += 2 * c; M[v, v] += 2 * c; M[u, v] += c; M[v, u] += c;
                }
            }
            return M;
        }

        private Matrix<double> BuildCouplingMatrix(FEMMesh mesh, BoundaryConditions bc)
        {
            var A = Matrix<double>.Build.Sparse(mesh.Vertices.Count, bc.Electrodes.Count);
            foreach (var electrode in bc.Electrodes)
            {
                if (Math.Abs(electrode.ZContact) < 1e-12) continue;
                var electrodeEdges = FindElectrodeEdges(mesh, electrode);
                foreach (var edge in electrodeEdges)
                {
                    double length = Math.Sqrt(Math.Pow(edge.End.X - edge.Start.X, 2) + Math.Pow(edge.End.Y - edge.Start.Y, 2));
                    A[edge.Start.GlobalId, electrode.Id] += length / (2.0 * electrode.ZContact);
                    A[edge.End.GlobalId, electrode.Id] += length / (2.0 * electrode.ZContact);
                }
            }
            return A;
        }

        private Matrix<double> BuildElectrodeMatrix(FEMMesh mesh, BoundaryConditions bc)
        {
            var D = Matrix<double>.Build.Sparse(bc.Electrodes.Count, bc.Electrodes.Count);
            foreach (var electrode in bc.Electrodes)
            {
                if (Math.Abs(electrode.ZContact) < 1e-12) continue;
                var electrodeEdges = FindElectrodeEdges(mesh, electrode);
                double totalLength = electrodeEdges.Sum(e => Math.Sqrt(Math.Pow(e.End.X - e.Start.X, 2) + Math.Pow(e.End.Y - e.Start.Y, 2)));
                D[electrode.Id, electrode.Id] = totalLength / electrode.ZContact;
            }
            return D;
        }

        private Matrix<double> BuildSystemMatrix(Matrix<double> K, Matrix<double> M, Matrix<double> A, Matrix<double> D, int numVertices, int numElectrodes)
        {
            int systemSize = numVertices + numElectrodes;
            var S = Matrix<double>.Build.Sparse(systemSize, systemSize);
            S.SetSubMatrix(0, 0, K + M);
            S.SetSubMatrix(0, numVertices, -A);
            S.SetSubMatrix(numVertices, 0, -A.Transpose());
            S.SetSubMatrix(numVertices, numVertices, D);
            return S;
        }

        private Vector<double> BuildRhsVector(BoundaryConditions bc, int numVertices, Vector<double> potentialSource)
        {
            int systemSize = numVertices + bc.Electrodes.Count;
            var F = Vector<double>.Build.Dense(systemSize);
            if (potentialSource != null)
                F.SetSubVector(0, numVertices, potentialSource);
            for (int i = 0; i < bc.Electrodes.Count; i++)
                F[numVertices + i] = bc.Electrodes[i].Current;
            return F;
        }

        private (Matrix<double>, Vector<double>) ApplyGrounding(Matrix<double> S, Vector<double> F, int numVertices, int groundElectrodeId)
        {
            int groundIndex = numVertices + groundElectrodeId;
            var indicesToRemove = new[] { groundIndex };

            var groundElement = F.ElementAt(groundIndex);

            groundElement = 0.0;

            return (S.RemoveRow(groundIndex).RemoveColumn(groundIndex), F);
        }

        private Vector<double> ReconstructFullSolution(double[] solution_grounded, int numVertices, int numElectrodes, int groundElectrodeId)
        {
            var fullSolution = new List<double>(solution_grounded);
            fullSolution.Insert(numVertices + groundElectrodeId, 0.0);
            return Vector<double>.Build.DenseOfEnumerable(fullSolution);
        }

        private List<Edge> FindElectrodeEdges(FEMMesh mesh, Electrode electrode)
        {
            var electrodeEdges = new List<Edge>();
            var electrodeVertexIds = new HashSet<int>(electrode.VertexIds);
            foreach (var element in mesh.Elements.Cast<FEMElement>())
            {
                var v = element.Vertices;
                if (electrodeVertexIds.Contains(v[0].GlobalId) && electrodeVertexIds.Contains(v[1].GlobalId)) 
                    electrodeEdges.Add(new Edge(v[0], v[1]));
                if (electrodeVertexIds.Contains(v[1].GlobalId) && electrodeVertexIds.Contains(v[2].GlobalId)) 
                    electrodeEdges.Add(new Edge(v[1], v[2]));
                if (electrodeVertexIds.Contains(v[2].GlobalId) && electrodeVertexIds.Contains(v[0].GlobalId)) 
                    electrodeEdges.Add(new Edge(v[2], v[0]));
            }
            return electrodeEdges.GroupBy(e => (Math.Min(e.Start.GlobalId, e.End.GlobalId), Math.Max(e.Start.GlobalId, e.End.GlobalId))).Select(g => g.First()).ToList();
        }

        private List<(int NodeId, double Value)> FindPinnedNodes(FEMMesh mesh)
        {
            var pinnedNodes = new List<(int, double)>();
            foreach (var element in mesh.Elements.Cast<FEMElement>())
            {
                if (element.IsPinned)
                    foreach (var vertex in element.Vertices)
                        if (!pinnedNodes.Any(n => n.Item1 == vertex.GlobalId))
                            pinnedNodes.Add((vertex.GlobalId, element.PinValue));
            }
            return pinnedNodes;
        }

        private void ApplyDirichletConditions(Matrix<double> A, Vector<double> b, List<(int NodeId, double Value)> conditions)
        {
            foreach (var (nodeId, val) in conditions)
            {
                for (int j = 0; j < A.RowCount; j++)
                    if (j != nodeId)
                        b[j] -= A[j, nodeId] * val;

                A.ClearRow(nodeId);
                A.ClearColumn(nodeId);
                A[nodeId, nodeId] = 1.0;
                b[nodeId] = val;
            }
        }

        #endregion
    }
}
