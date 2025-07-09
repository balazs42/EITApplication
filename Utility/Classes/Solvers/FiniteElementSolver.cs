using System.Diagnostics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// The core engine for all FEM-based calculations implementing the 
    /// Complete Electrode Model (CEM) for forward and adjoint solves.
    /// References:
    ///  • Stiffness assembly: Eq. (1.2.3)
    ///  • Robin mass (contact impedance): Eq. (1.1.12)
    ///  • Coupling matrix:         Eq. (1.1.12)
    ///  • Electrode diag. matrix:  Eq. (1.1.15)
    ///  • Block system form:       Eq. (1.1.16)
    ///  • Grounding removal:       Sec. 1.1.3 (after Eq. 1.1.16)
    ///  • Conductivity gradient:   Eq. (2.1.20)
    /// </summary>
    public sealed class FiniteElementSolver
    {
        private readonly INumericSolver _numericSolver;

        /// <summary>
        /// Constructor takes a numeric linear solver (e.g. LU, SVD).
        /// </summary>
        public FiniteElementSolver(INumericSolver numericSolver)
        {
            _numericSolver = numericSolver;
        }

        /// <summary>
        /// Solve the CEM forward problem: nodal potentials α and electrode voltages U.
        /// Assembles K, M, A, D and RHS F, removes the ground row/column, solves,
        /// then reinserts U_ground = 0 (see Sec. 1.1.3).
        /// </summary>
        public PotentialDistribution SolveSystem(IMesh mesh, ConductivityDistribution sigma, List<Electrode> electrodes, Vector<double>? potentialSourceTerm = null)
        {
            if (mesh is not FEMMesh femMesh)
                throw new ArgumentException("Requires FEMMesh", nameof(mesh));

            int N = femMesh.Vertices.Count;          // number of domain DOFs
            int L = electrodes.Count;                // number of electrodes
            int groundId = electrodes.Find(e => e.IsGround).Id;

            // 1) Build sub-blocks
            var K = BuildStiffnessMatrix(femMesh, sigma);               // Eq. (1.2.3)
            var M = BuildRobinMassMatrix(femMesh, electrodes);          // Eq. (1.1.12)
            var A = BuildCouplingMatrix(femMesh, electrodes);           // Eq. (1.1.12)
            var D = BuildElectrodeMatrix(femMesh, electrodes);          // Eq. (1.1.15)

            Debug.WriteLine("Stiffness matrix K:\n" + K.ToMatrixString());
            Debug.WriteLine("Robin mass matrix M:\n" + M.ToMatrixString());
            Debug.WriteLine("Coupling matrix A:\n" + A.ToMatrixString());
            Debug.WriteLine("Electrode matrix D:\n" + D.ToMatrixString());

            // 2) Assemble global saddle-point system S [α; U] = F  (Eq. 1.1.16)
            var S = BuildSystemMatrix(K, M, A, D, N, L);
            var F = BuildRhsVector(electrodes, N, potentialSourceTerm);

            Debug.WriteLine("System matrix S:\n" + S.ToMatrixString());
            Debug.WriteLine("RHS vector F:\n" + F.ToVectorString());

            // 3) Remove the ground electrode row/column and RHS entry (Sec. 1.1.3)
            var (Sg, Fg) = ApplyGrounding(S, F, N, groundId);
            Debug.WriteLine("Grounded system Sg:\n" + Sg.ToMatrixString());
            Debug.WriteLine("Grounded RHS Fg:\n" + Fg.ToVectorString());

            // 4) Solve the smaller, non-singular system
            var solg = _numericSolver.SolveLinearSystem(Sg.ToArray(), Fg.ToArray());

            // 5) Reconstruct full solution including U_ground=0
            var full = ReconstructFullSolution(solg, N, L, groundId);
            Debug.WriteLine("Full solution vector [α; U]:\n" + full.ToVectorString());

            // 6) Extract nodal potentials α and build the distribution
            var potentials = full.SubVector(0, N);
            var dict = new Dictionary<int, double>(N);
            for (int i = 0; i < N; i++)
                dict[femMesh.Vertices[i].GlobalId] = potentials[i];

            return new PotentialDistribution(dict);
        }

        /// <summary>
        /// Compute the element-wise gradient of the misfit via adjoint φ, μ:
        /// ∇J/∇σ = ∇μ·∇φ  (Eq. 2.1.20)
        /// </summary>
        public ConductivityDistribution ComputeGradient(IMesh mesh, PotentialDistribution phi, PotentialDistribution mu)
        {
            if (mesh is not FEMMesh femMesh)
                throw new ArgumentException("Requires FEMMesh", nameof(mesh));

            var gradDict = new Dictionary<int, double>();

            // Loop over each triangular element
            foreach (var elem in femMesh.Elements)
            {
                // Vertex coords
                var v = elem.Vertices;
                double x1 = v[0].X, y1 = v[0].Y;
                double x2 = v[1].X, y2 = v[1].Y;
                double x3 = v[2].X, y3 = v[2].Y;
                double area = elem.Area;

                // Eq. (1.2.2): shape function gradients (constant per element)
                var grad1 = new double[] { (y2 - y3) / (2 * area), (x3 - x2) / (2 * area) };
                var grad2 = new double[] { (y3 - y1) / (2 * area), (x1 - x3) / (2 * area) };
                var grad3 = new double[] { (y1 - y2) / (2 * area), (x2 - x1) / (2 * area) };

                // Local nodal potentials
                double phi1 = phi.GetPotential(v[0].GlobalId);
                double phi2 = phi.GetPotential(v[1].GlobalId);
                double phi3 = phi.GetPotential(v[2].GlobalId);
                double mu1 = mu.GetPotential(v[0].GlobalId);
                double mu2 = mu.GetPotential(v[1].GlobalId);
                double mu3 = mu.GetPotential(v[2].GlobalId);

                // Compute ∇φ_h and ∇μ_h: sums of nodal alpha * grad(phi_i)
                var gradPhi = new double[2]; // ∇φ_h
                var gradMu = new double[2]; // ∇μ_h
                for (int d = 0; d < 2; d++)
                {
                    gradPhi[d] = phi1 * grad1[d] + phi2 * grad2[d] + phi3 * grad3[d];
                    gradMu[d] = mu1 * grad1[d] + mu2 * grad2[d] + mu3 * grad3[d];
                }

                // Eq. (2.1.20): ∇J/∇σ on element = (∇μ·∇φ) * |T|
                double localGrad = (gradMu[0] * gradPhi[0] + gradMu[1] * gradPhi[1]) * area;
                gradDict[elem.Id] = localGrad;
            }

            var result = new ConductivityDistribution(gradDict);
            // Debug print gradient distribution
            Debug.WriteLine("Gradient per element:\n");
            foreach (var kvp in gradDict)
                Debug.WriteLine($"Element {kvp.Key}: ∂J/∂σ = {kvp.Value:0.0000}");

            return result;
        }

        #region Matrix Assembly Helpers

        /// <summary>
        /// Build the stiffness matrix K: 
        /// K_ij = σ_T * (∇φ_i · ∇φ_j) * |T|  (Eq. 1.2.3)
        /// </summary>
        private Matrix<double> BuildStiffnessMatrix(FEMMesh mesh, ConductivityDistribution sigma)
        {
            int N = mesh.Vertices.Count;
            var K = DenseMatrix.Build.Dense(N, N);

            // Loop elements
            foreach (var elem in mesh.Elements)
            {
                var v = elem.Vertices;
                double x1 = v[0].X, y1 = v[0].Y;
                double x2 = v[1].X, y2 = v[1].Y;
                double x3 = v[2].X, y3 = v[2].Y;
                double area = elem.Area;

                // shape function gradients
                var g1 = new double[] { (y2 - y3) / (2 * area), (x3 - x2) / (2 * area) };
                var g2 = new double[] { (y3 - y1) / (2 * area), (x1 - x3) / (2 * area) };
                var g3 = new double[] { (y1 - y2) / (2 * area), (x2 - x1) / (2 * area) };

                // conductivity on this element
                double sigmaT = sigma.GetConductivity(elem.Id);

                // local stiffness 3x3
                double[,] loc = new double[3, 3];
                var grads = new[] { g1, g2, g3 };
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        // Eq. (1.2.3)
                        loc[i, j] = sigmaT * (grads[i][0] * grads[j][0] + grads[i][1] * grads[j][1]) * area;
                    }

                // assemble into global K
                var ids = new[] { v[0].GlobalId, v[1].GlobalId, v[2].GlobalId };
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        K[ids[i], ids[j]] += loc[i, j];
            }
            return K;
        }

        /// <summary>
        /// Build the Robin mass matrix M for contact impedances:
        /// lumped: M[ii] += 1/z_ℓ  per electrode node  (Eq. 1.1.12)
        /// </summary>
        private Matrix<double> BuildRobinMassMatrix(FEMMesh mesh, List<Electrode> electrodes)
        {
            int N = mesh.Vertices.Count;
            var M = DenseMatrix.Build.Dense(N, N);

            // lump each electrode node
            foreach (var el in electrodes)
            {
                if (el.ZContact <= 0) continue;
                double factor = 1.0 / el.ZContact;
                foreach (var vid in el.VertexIds)
                {
                    // Eq. (1.1.12) lumped diagonal
                    M[vid, vid] += factor;
                }
            }
            return M;
        }

        /// <summary>
        /// Build the coupling matrix A between domain nodes α and electrodes U:
        /// lumped: A[vid, el.Id] += 1/zℓ  (Eq. 1.1.12)
        /// </summary>
        private Matrix<double> BuildCouplingMatrix(FEMMesh mesh, List<Electrode> electrodes)
        {
            int N = mesh.Vertices.Count;
            int L = electrodes.Count;
            var A = DenseMatrix.Build.Dense(N, L);

            foreach (var el in electrodes)
            {
                if (el.ZContact <= 0) continue;
                double factor = 1.0 / el.ZContact;
                foreach (var vid in el.VertexIds)
                {
                    // Eq. (1.1.12)
                    A[vid, el.Id] += factor;
                }
            }
            return A;
        }

        /// <summary>
        /// Build the diagonal electrode matrix D:
        /// D[ℓ,ℓ] = |Eℓ| / zℓ  ≈ (#nodes) / zℓ  (Eq. 1.1.15)
        /// </summary>
        private Matrix<double> BuildElectrodeMatrix(FEMMesh mesh, List<Electrode> electrodes)
        {
            int L = electrodes.Count;
            var D = DenseMatrix.Build.Dense(L, L);
            foreach (var el in electrodes)
            {
                if (el.ZContact <= 0) continue;
                // approximate |Eℓ| ≈ number of nodes
                double lengthApprox = el.VertexIds.Count;
                D[el.Id, el.Id] = lengthApprox / el.ZContact;  // Eq. (1.1.15)
            }
            return D;
        }

        /// <summary>
        /// Assemble block system S = [K+M  -A; -Aᵀ  D]  (Eq. 1.1.16)
        /// </summary>
        private Matrix<double> BuildSystemMatrix(
            Matrix<double> K,
            Matrix<double> M,
            Matrix<double> A,
            Matrix<double> D,
            int numVertices,
            int numElectrodes)
        {
            int size = numVertices + numElectrodes;
            var S = DenseMatrix.Build.Dense(size, size);

            // Top-left: K+M
            var KM = K + M;
            S.SetSubMatrix(0, numVertices, 0, numVertices, KM);

            // Top-right: -A
            S.SetSubMatrix(0, numVertices, numVertices, numElectrodes, A.Multiply(-1.0));

            // Bottom-left: -Aᵀ
            S.SetSubMatrix(numVertices, numElectrodes, 0, numVertices, A.Transpose().Multiply(-1.0));

            // Bottom-right: D
            S.SetSubMatrix(numVertices, numElectrodes, numVertices, numElectrodes, D);

            return S;
        }

        /// <summary>
        /// Build RHS F = [potentialSource; electrode currents]  (bottom block = Iₗ)
        /// </summary>
        private Vector<double> BuildRhsVector(
            List<Electrode> electrodes,
            int numVertices,
            Vector<double>? potentialSource)
        {
            int L = electrodes.Count;
            var F = DenseVector.Build.Dense(numVertices + L);

            // Top: domain source term
            if (potentialSource != null)
                F.SetSubVector(0, numVertices, potentialSource);

            // Bottom: electrode currents Iₗ
            for (int i = 0; i < L; i++)
            {
                F[numVertices + electrodes[i].Id] = electrodes[i].Current;  // Eq. (1.1.16)
            }
            return F;
        }

        #endregion

        #region Grounding and Solution Reconstruction

        /// <summary>
        /// Remove the ground electrode DOF (row&column) and RHS entry (Sec. 1.1.3).
        /// </summary>
        private (Matrix<double> Sg, Vector<double> Fg) ApplyGrounding(
            Matrix<double> S,
            Vector<double> F,
            int numVertices,
            int groundElectrodeId)
        {
            int fullSize = S.RowCount;
            int removeIndex = numVertices + groundElectrodeId;
            int newSize = fullSize - 1;

            var Sg = DenseMatrix.Build.Dense(newSize, newSize);
            var Fg = DenseVector.Build.Dense(newSize);

            // Copy rows and cols skipping grounded index
            for (int i = 0, ri = 0; i < fullSize; i++)
            {
                if (i == removeIndex) continue;
                for (int j = 0, cj = 0; j < fullSize; j++)
                {
                    if (j == removeIndex) continue;
                    Sg[ri, cj] = S[i, j];
                    cj++;
                }
                Fg[ri] = F[i];
                ri++;
            }
            return (Sg, Fg);
        }

        /// <summary>
        /// Reinsert U_ground=0 back into the solution vector of length N+L (after solve).
        /// </summary>
        private Vector<double> ReconstructFullSolution(
            double[] solg,
            int numVertices,
            int numElectrodes,
            int groundElectrodeId)
        {
            var list = new List<double>(solg);
            list.Insert(numVertices + groundElectrodeId, 0.0);
            return Vector<double>.Build.DenseOfEnumerable(list);
        }

        #endregion
    }
}
