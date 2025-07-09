using Google.OrTools.ConstraintSolver;
using System.Diagnostics;
using System.Numerics;
using System.Xml.Linq;
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
        private FEMMesh _mesh;
        private readonly INumericSolver _numericSolver;
        public Complex[,] K { get; set; }           // Stiffness matrix
        public Complex[,] M { get; set; }           // Robin boundary condition matrix
        public Complex[,] A_couple { get; set; }    // Coupling matrix
        public Complex[,] D { get; set; }           // Diagonal matrix associated with electrode term
        public Complex[] Alpha { get; set; }        // Coefficients for φ
        public Complex[] U { get; set; }            // Constant electrode potentials on each electrode
        public Complex[] I { get; set; }            // Net current on electrodes
        public Complex[,] S { get; set; }           // System matrix
        public Complex[] b { get; set; }            // System vector
        public int N_phi { get; set; } = 0;         // Number of φ coefficients
        public int L { get; set; } = 0;             // Number of electrodes

        /// <summary>
        /// Constructor takes a numeric linear solver (e.g. LU, SVD).
        /// </summary>
        public FiniteElementSolver(FEMMesh mesh, INumericSolver numericSolver)
        {
            _mesh = mesh;
            _numericSolver = numericSolver;

            // Number of unknown potential vertices inside the domain
            N_phi = _mesh.Vertices.Count();

            // Number of electrodes on the surface 
            L = _mesh.Electrodes.Count();

            if (N_phi <= 0 || L <= 0)
                throw new ArgumentException(nameof(mesh), "N_phi or N_lambda was 0 during initialization, check mesh generation errors!");

            // Allocating matrices and vectors
            K = new Complex[N_phi, N_phi];
            M = new Complex[N_phi, N_phi];
            A_couple = new Complex[N_phi, L];
            D = new Complex[L, L];
            Alpha = new Complex[N_phi];
            U = new Complex[L];
            I = new Complex[L];
            S = new Complex[N_phi + L, N_phi + L];
            b = new Complex[N_phi + L];

        }

        /// <summary>
        /// Solve the CEM forward problem: nodal potentials α and electrode voltages U.
        /// Assembles K, M, A, D and RHS F, removes the ground row/column, solves,
        /// then reinserts U_ground = 0 (see Sec. 1.1.3).
        /// </summary>
        public PotentialDistribution SolveSystem(IMesh mesh, ConductivityDistribution sigma, List<Electrode> electrodes, Complex[] potentialSourceTerm = null)
        {
            if (mesh is not FEMMesh femMesh)
                throw new ArgumentException("Requires FEMMesh", nameof(mesh));

            var groundElectrode = electrodes.Find(e => e.IsGround);

            if (groundElectrode == null)
                throw new ArgumentNullException("Ground electrode is not specified on mesh, check code!");

            int groundId = groundElectrode.Id;

            // 1) Build sub-blocks
            BuildStiffnessMatrix(femMesh);          // Eq. (1.2.3)
            BuildRobinMassMatrix(femMesh);          // Eq. (1.1.12)
            BuildCouplingMatrix(femMesh);           // Eq. (1.1.12)
            BuildElectrodeMatrix(femMesh);          // Eq. (1.1.15)

            // 2) Assemble global saddle-point system S [α; U] = F  (Eq. 1.1.16)
            BuildSystemMatrix();
            BuildRhsVector(electrodes, potentialSourceTerm);

            // 3) Remove the ground electrode row/column and RHS entry (Sec. 1.1.3)
            var (Sg, Fg) = ApplyGrounding(S, b, groundId);

            // 4) Solve the smaller, non-singular system
            var solg = _numericSolver.SolveLinearSystem(MatrixToReal(Sg), VectorToReal(Fg));

            // 5) Reconstruct full solution including U_ground=0
            var full = ReconstructFullSolution(solg, groundId);

            double[] potentials = VectorToReal(full);

            // 6) Extract nodal potentials α and build the distribution
            var dict = new Dictionary<int, double>(N_phi);
            for (int i = 0; i < N_phi; i++)
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

        #region Stiffness Matrix Assembly
        /// <summary>
        /// Build the stiffness matrix K: 
        /// K_ij = σ_T * (∇φ_i · ∇φ_j) * |T|  (Eq. 1.2.3)
        /// </summary>
        private void BuildStiffnessMatrix(FEMMesh mesh)
        {
            // Reallocate to avoid any overwrite problems
            K = new Complex[N_phi, N_phi];

            // Get each local stiffness matrix and add it to the global stiffness matrix
            foreach (FEMElement element in mesh.Elements)
            {
                int rowIndex = 0;
                int colIndex = 0;

                // Calculate the local stiffness matrix
                double[,] localStiffnessMatrix = CalculateElementStiffnessMatrix(element);

                // Retrieve the vertices of the element
                Vertex[] elementVertices = { element.Vertices[0], element.Vertices[1], element.Vertices[2] };

                // Add the local stiffness matrix to the global stiffness matrix
                for (int i = 0; i < 3; i++)
                {
                    // Row index is the global index of the i-th vertex
                    rowIndex = elementVertices[i].GlobalId;

                    for (int j = 0; j < 3; j++)
                    {
                        // Column index is the global index of the j-th vertex
                        colIndex = elementVertices[j].GlobalId;
                        
                        K[rowIndex, colIndex] += localStiffnessMatrix[i, j];
                    }
                }
            }
        }

        // Calculates the 3x3 stiffness matrix for the given element
        private double[,] CalculateElementStiffnessMatrix(FEMElement element)
        {
            // Get the area of the element
            double area = element.Area;

            // Get the gradient vectors from the given element
            double[,] gradients = element.GradPhi;

            // Get the conductivity of the given element
            double sigma = element.Conductivity;

            // Create local stiffness matrix, that will store tha values
            double[,] localStiffnessMatrix = new double[3, 3];

            // Store the dotproduct of the gradients
            double[,] dotProducts = element.DotProducts;

            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    localStiffnessMatrix[i, j] = sigma * area * dotProducts[i, j]; // K_ij^e = \sigma_T |T| [\grad(\phi_i^T \cdot \grad(\phi_j^T))]

            return localStiffnessMatrix;
        }
        #endregion

        /// <summary>
        /// Build the Robin mass matrix M for contact impedances:
        /// M[km] += 1/z_ℓ \int_{E_{\ell}}\varphi_k\varphi_m per electrode node  (Eq. 1.1.12)
        /// </summary>
        private void BuildRobinMassMatrix(FEMMesh mesh)
        {
            // Reallocate, to avoid any overwrite problems
            M = new Complex[N_phi, N_phi];

            List<Vertex> electrodeVertices = mesh.Vertices.FindAll(x => x.IsElectrode).ToList();

            // Iteratre through each electrode
            foreach(var vertex in electrodeVertices)
            {
                Electrode? e = mesh.Electrodes.Find(x => x.MeshId == vertex.GlobalId);

                if (e == null)
                    throw new NullReferenceException("No electrode found with corresponding global id, check code!");

                // Get the contact impedance for the electrode
                double zContact = (e.ZContact > 0.0) ? e.ZContact : throw new ArgumentOutOfRangeException("Contact impedance should be specified, check code!");

                // Get the surface length, so we can calculate the integral 
                double surfaceLength = e.BoundarySurfaceLength;

                // Find the vertices which also lie on the boundary and are also neighbros to the vertex corresponding to electrode
                List<Vertex> boundaryNeighbors = vertex.Neighbors.FindAll(x => x.IsBoundary).ToList();

                if (boundaryNeighbors.Count != 2)
                    throw new ArgumentOutOfRangeException("There should be exactly 2 boundary neighbors, check code!");

                Vertex varphi_m1 = boundaryNeighbors[0];
                Vertex varphi_m2 = boundaryNeighbors[1];

                int k = vertex.GlobalId;
                int m1 = varphi_m1.GlobalId;
                int m2 = varphi_m2.GlobalId;

                double m1Distance = Math.Sqrt(Math.Pow(vertex.X - varphi_m1.X, 2) + Math.Pow(vertex.Y - varphi_m1.Y, 2));
                double m2Distance = Math.Sqrt(Math.Pow(vertex.X - varphi_m2.X, 2) + Math.Pow(vertex.Y - varphi_m2.Y, 2));

                M[k, m1] = (1.0 / zContact) * m1Distance * (1.0 - surfaceLength);
                M[k, m2] = (1.0 / zContact) * m2Distance * (1.0 - surfaceLength);
            }
        }

        /// <summary>
        /// Build the coupling matrix A between domain nodes α and electrodes U:
        /// lumped: A[vid, el.Id] += 1/zℓ  (Eq. 1.1.12)
        /// </summary>
        private void BuildCouplingMatrix(FEMMesh mesh)
        {
            A_couple = new Complex[N_phi, L];

            List<Vertex> electrodeVertices = mesh.Vertices.FindAll(x => x.IsElectrode).ToList();

            // Iteratre through each electrode
            foreach (var vertex in electrodeVertices)
            {
                Electrode? e = mesh.Electrodes.Find(x => x.MeshId == vertex.GlobalId);

                if (e == null)
                    throw new NullReferenceException("No electrode found with corresponding global id, check code!");

                // Get the contact impedance for the electrode
                double zContact = (e.ZContact > 0.0) ? e.ZContact : throw new ArgumentOutOfRangeException("Contact impedance should be specified, check code!");

                // Get the surface length, so we can calculate the integral 
                double surfaceLength = e.BoundarySurfaceLength;

                // Find the vertices which also lie on the boundary and are also neighbros to the vertex corresponding to electrode
                Vertex? domainNeighbor = vertex.Neighbors.Find(x => !x.IsBoundary);

                if (domainNeighbor == null)
                    throw new NullReferenceException("Boundary vertex does not have any domain neighbors, check code!");

                int ell = vertex.ElectrodeId;
                int m = domainNeighbor.GlobalId;

                A_couple[m, ell] = (1.0 / zContact) * surfaceLength; 
            }
        }

        /// <summary>
        /// Build the diagonal electrode matrix D:
        /// D[ℓ,ℓ] = |Eℓ| / zℓ  ≈ (#nodes) / zℓ  (Eq. 1.1.15)
        /// </summary>
        private void BuildElectrodeMatrix(FEMMesh mesh)
        {
            D = new Complex[L, L];

            List<Vertex> electrodeVertices = mesh.Vertices.FindAll(x => x.IsElectrode).ToList();

            foreach(var vertex in electrodeVertices)
            {
                Electrode? e = mesh.Electrodes.Find(x => x.MeshId == vertex.GlobalId);

                // Get the contact impedance for the electrode
                double zContact = (e.ZContact > 0.0) ? e.ZContact : throw new ArgumentOutOfRangeException("Contact impedance should be specified, check code!");

                // Get the surface length, so we can calculate the integral 
                double surfaceLength = e.BoundarySurfaceLength;

                // Find the vertices which also lie on the boundary and are also neighbros to the vertex corresponding to electrode
                List<Vertex> boundaryNeighbors = vertex.Neighbors.FindAll(x => x.IsBoundary).ToList();

                if (boundaryNeighbors.Count != 2)
                    throw new ArgumentOutOfRangeException("There should be exactly 2 boundary neighbors, check code!");

                Vertex varphi_m1 = boundaryNeighbors[0];
                Vertex varphi_m2 = boundaryNeighbors[1];

                double m1Distance = Math.Sqrt(Math.Pow(vertex.X - varphi_m1.X, 2) + Math.Pow(vertex.Y - varphi_m1.Y, 2));
                double m2Distance = Math.Sqrt(Math.Pow(vertex.X - varphi_m2.X, 2) + Math.Pow(vertex.Y - varphi_m2.Y, 2));

                int ell = vertex.ElectrodeId;

                D[ell, ell] = (m1Distance + m2Distance) * surfaceLength / zContact;
            }
        }

        /// <summary>
        /// Assemble block system S = [K+M  -A; -Aᵀ  D]  (Eq. 1.1.16)
        /// </summary>
        private void BuildSystemMatrix()
        {
            int size = N_phi + L;
            S = new Complex[size, size]; 

            // Top-left: K+M
            for (int i = 0; i < N_phi; i++) 
                for (int j = 0; j < N_phi; j++)
                    S[i, j] = K[i, j] + M[i, j];
            // Top-right: -A_coup
            for (int i = 0; i < N_phi; i++) 
                for (int ell = 0; ell < L; ell++)
                    S[i, N_phi + ell] = -A_couple[i, ell];
            // Bottom-left: -(A_coup)^T
            for (int ell = 0; ell < L; ell++)
                for (int i = 0; i < N_phi; i++)
                    S[N_phi + ell, i] = -A_couple[i, ell];
            // Bottom-right: D
            for (int ell = 0; ell < L; ell++)
                for (int m = 0; m < L; m++)
                    S[N_phi + ell, N_phi + m] = D[ell, m];

            Debug.WriteLine("SystemMatrix S:\n" + FormatComplexMatrix(S));
        }

        /// <summary>
        /// Build RHS F = [potentialSource; electrode currents]  (bottom block = Iₗ)
        /// </summary>
        private void BuildRhsVector(List<Electrode> electrodes, Complex[] potentialSourceTerm)
        {
            // b = [0_{N_phi}; I_ell]
            Array.Clear(b, 0, b.Length);
            for (int ell = 0; ell < L; ell++)
                b[N_phi + ell] = electrodes[ell].Current;
            Debug.WriteLine("RHS b:\n" + FormatComplexVector(b));
        }

        #endregion

        #region Grounding and Solution Reconstruction
        /// <summary>
        /// Remove ground DOF: delete row/col at idx = N_phi + groundId (Sec. 1.1.3)
        /// </summary>
        private (Complex[,], Complex[]) ApplyGrounding(Complex[,] A, Complex[] b, int groundId)
        {
            int size = N_phi + L;               // “size” system size (number of nodal DOFs + number of electrodes)
            int deleteId = N_phi + groundId;    // the global index of the electrode DOF we’re removing
            int reducedSize = size - 1;         // “reduced” system size after removing one row & column

            var Ar = new Complex[reducedSize, reducedSize];
            var br = new Complex[reducedSize];
            for (int i = 0, ii = 0; i < size; i++)
            {
                if (i == deleteId) continue;
                br[ii] = b[i];
                for (int j = 0, jj = 0; j < size; j++)
                {
                    if (j == deleteId) continue;
                    Ar[ii, jj] = A[i, j];
                    jj++;
                }
                ii++;
            }
            Debug.WriteLine("Grounded A (size=" + reducedSize + "):\n" + FormatComplexMatrix(Ar));
            Debug.WriteLine("Grounded b (size=" + reducedSize + "):\n" + FormatComplexVector(br));
            return (Ar, br);
        }


        /// <summary>
        /// Reinsert zero-voltage at ground index to full solution array
        /// </summary>
        private Complex[] ReconstructFullSolution(double[] solRed, int groundId)
        {
            int size = N_phi + L;
            var fullSol = new Complex[size];
            for (int i = 0, ir = 0; i < size; i++)
            {
                if (i == N_phi + groundId)
                    fullSol[i] = Complex.Zero;
                else
                    fullSol[i] = solRed[ir++];
            }
            return fullSol;
        }

        #endregion
        #region Utilities

        private static double[,] MatrixToReal(Complex[,] C)
        {
            int r = C.GetLength(0), c = C.GetLength(1);
            var R = new double[r, c];
            for (int i = 0; i < r; i++) for (int j = 0; j < c; j++) R[i, j] = C[i, j].Real;
            return R;
        }
        private static double[] VectorToReal(Complex[] C)
        {
            int n = C.Length; var R = new double[n];
            for (int i = 0; i < n; i++) R[i] = C[i].Real;
            return R;
        }

        private static string FormatComplexMatrix(Complex[,] M)
        {
            int r = M.GetLength(0), c = M.GetLength(1);
            var s = "";
            for (int i = 0; i < r; i++)
            {
                for (int j = 0; j < c; j++) s += M[i, j].ToString("0.###") + " ";
                s += "\n";
            }
            return s;
        }
        private static string FormatComplexVector(Complex[] v)
        {
            var s = "";
            for (int i = 0; i < v.Length; i++) s += v[i].ToString("0.###") + "\n";
            return s;
        }

        #endregion
    }
}
