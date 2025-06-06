using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers
{
    public sealed class FiniteElementSolver
    {
        private readonly INumericSolver _numericSolver;

        /// <summary>
        /// Initializes the solver with a specific method for solving linear systems (e.g., LU, SVD).
        /// </summary>
        /// <param name="numericSolver">The numerical solver to use.</param>
        public FiniteElementSolver(INumericSolver numericSolver)
        {
            _numericSolver = numericSolver;
        }

        /// <summary>
        /// Solves the forward EIT problem using the Finite Element Method for the Complete Electrode Model.
        /// Assembles and solves the block saddle-point system from equation (1.1.16).
        /// </summary>
        public PotentialDistribution ForwardSolve(IMesh mesh, ConductivityDistribution conductivityDistribution, BoundaryConditions boundaryConditions)
        {
            if (mesh is not FEMMesh femMesh)
                throw new ArgumentException("FiniteElementSolver requires an FEMMesh, check code!", nameof(mesh));

            int numVertices = femMesh.Vertices.Count;
            int numElectrodes = boundaryConditions.Electrodes.Count;
            int groundElectrodeId = numElectrodes - 1; // Convention: ground the last electrode

            // 1. Assemble the components of the system matrix
            var K = BuildStiffnessMatrix(femMesh, conductivityDistribution);
            var M = BuildRobinMassMatrix(femMesh, boundaryConditions);
            var A = BuildCouplingMatrix(femMesh, boundaryConditions);
            var D = BuildElectrodeMatrix(femMesh, boundaryConditions);

            // 2. Build the full system matrix S = [ K+M   -A ]
            //                                     [ -A'    D ]
            var S = BuildSystemMatrix(K, M, A, D, numVertices, numElectrodes);

            // 3. Build the Right-Hand-Side (RHS) vector F = [ 0, I ]'
            var F = BuildRhsVector(boundaryConditions, numVertices);

            // 4. Apply grounding to make the system solvable
            var (S_grounded, F_grounded) = ApplyGrounding(S, F, numVertices, groundElectrodeId);

            // 5. Solve the linear system Ax = b
            double[] solution_grounded = _numericSolver.SolveLinearSystem(S_grounded.ToArray(), F_grounded.ToArray());

            // 6. Reconstruct the full solution vector (including the grounded potential)
            Vector<double> fullSolution = ReconstructFullSolution(solution_grounded, numVertices, numElectrodes, groundElectrodeId);

            // 7. Extract nodal potentials (α) and package the result
            var nodalPotentials = fullSolution.SubVector(0, numVertices);
            var potentialDict = new Dictionary<int, double>();

            for (int i = 0; i < numVertices; i++)
                potentialDict[femMesh.Vertices[i].GlobalId] = nodalPotentials[i];

            return new PotentialDistribution(potentialDict);
        }

        // --- Matrix Assembly Helpers ---

        private Matrix<double> BuildStiffnessMatrix(FEMMesh mesh, ConductivityDistribution sigma)
        {
            var K = Matrix<double>.Build.Sparse(mesh.Vertices.Count, mesh.Vertices.Count);
            foreach (var element in mesh.Elements)
            {
                double conductivity = sigma.GetConductivity(element.Id);
                var v = new[] { element.V1, element.V2, element.V3 };

                // Gradients of basis functions (dNi/dx, dNi/dy)
                var gradN = new (double dx, double dy)[3];
                gradN[0] = ((v[1].Y - v[2].Y), (v[2].X - v[1].X));
                gradN[1] = ((v[2].Y - v[0].Y), (v[0].X - v[2].X));
                gradN[2] = ((v[0].Y - v[1].Y), (v[1].X - v[0].X));

                // Local stiffness matrix k_ij = γ * Area * (∇Ni · ∇Nj)
                var kLocal = Matrix<double>.Build.Dense(3, 3);
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        double dotProduct = gradN[i].dx * gradN[j].dx + gradN[i].dy * gradN[j].dy;
                        kLocal[i, j] = conductivity * dotProduct / (4 * element.Area);
                    }
                }

                // Add local matrix to global stiffness matrix K
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
                if (Math.Abs(electrode.ZContact) < 1e-12) continue; // Skip if no contact impedance

                var electrodeEdges = FindElectrodeEdges(mesh, electrode);
                foreach (var edge in electrodeEdges)
                {
                    double length = Math.Sqrt(Math.Pow(edge.End.X - edge.Start.X, 2) + Math.Pow(edge.End.Y - edge.Start.Y, 2));
                    int u = edge.Start.GlobalId;
                    int v = edge.End.GlobalId;

                    // M_ij = (1/z) * ∫(Ni * Nj) ds. For linear elements, this is a known formula.
                    double c = length / (6.0 * electrode.ZContact);
                    M[u, u] += 2 * c;
                    M[v, v] += 2 * c;
                    M[u, v] += c;
                    M[v, u] += c;
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

                    // A_il = (1/z) * ∫(Ni) ds. Integral of a linear basis function over an edge is length/2.
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

                // D_ll = |E_l| / z_l
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

        private Vector<double> BuildRhsVector(BoundaryConditions bc, int numVertices)
        {
            int systemSize = numVertices + bc.Electrodes.Count;
            var F = Vector<double>.Build.Dense(systemSize);

            // F = [ 0, I ]'
            for (int i = 0; i < bc.Electrodes.Count; i++)
                F[numVertices + i] = bc.Electrodes[i].Current;

            return F;
        }

        private (Matrix<double>, Vector<double>) ApplyGrounding(Matrix<double> S, Vector<double> F, int numVertices, int groundElectrodeId)
        {
            int groundIndex = numVertices + groundElectrodeId;
            int groundedSize = S.RowCount - 1;

            var S_grounded = Matrix<double>.Build.Sparse(groundedSize, groundedSize);
            var F_grounded = Vector<double>.Build.Dense(groundedSize);

            int r_new = 0;
            for (int r = 0; r < S.RowCount; r++)
            {
                if (r == groundIndex) continue;
                int c_new = 0;
                for (int c = 0; c < S.ColumnCount; c++)
                {
                    if (c == groundIndex) continue;
                    S_grounded[r_new, c_new] = S[r, c];
                    c_new++;
                }
                F_grounded[r_new] = F[r];
                r_new++;
            }
            return (S_grounded, F_grounded);
        }

        private Vector<double> ReconstructFullSolution(double[] solution_grounded, int numVertices, int numElectrodes, int groundElectrodeId)
        {
            var fullSolution = new List<double>(solution_grounded);
            fullSolution.Insert(numVertices + groundElectrodeId, 0.0); // Insert ground potential U_g = 0
            return Vector<double>.Build.DenseOfEnumerable(fullSolution);
        }

        private List<Edge> FindElectrodeEdges(FEMMesh mesh, Electrode electrode)
        {
            var electrodeEdges = new List<Edge>();
            var electrodeVertexIds = new HashSet<int>(electrode.VertexIds);

            foreach (var element in mesh.Elements)
            {
                var v = new[] { element.V1, element.V2, element.V3 };
                var v_ids = v.Select(vert => vert.GlobalId).ToArray();

                if (electrodeVertexIds.Contains(v_ids[0]) && electrodeVertexIds.Contains(v_ids[1])) electrodeEdges.Add(new Edge(v[0], v[1]));
                if (electrodeVertexIds.Contains(v_ids[1]) && electrodeVertexIds.Contains(v_ids[2])) electrodeEdges.Add(new Edge(v[1], v[2]));
                if (electrodeVertexIds.Contains(v_ids[2]) && electrodeVertexIds.Contains(v_ids[0])) electrodeEdges.Add(new Edge(v[2], v[0]));
            }
            // This can add duplicate edges, so we need to return a distinct list
            return electrodeEdges.GroupBy(e => (Math.Min(e.Start.GlobalId, e.End.GlobalId), Math.Max(e.Start.GlobalId, e.End.GlobalId)))
                                 .Select(g => g.First())
                                 .ToList();
        }
    }
}
