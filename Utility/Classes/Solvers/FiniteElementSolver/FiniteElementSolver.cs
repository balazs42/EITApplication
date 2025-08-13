using System.Diagnostics;
using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Solvers.FiniteElementSolver
{
    /// <summary>
    /// Core FEM engine for the Complete Electrode Model (CEM).
    /// Equations referenced inline:
    ///  • Eq (1.2.3): stiffness K
    ///  • Eq (1.1.12): contact impedance M, coupling A_coup
    ///  • Eq (1.1.15): electrode diag D
    ///  • Eq (1.1.16): assemble block system S
    ///  • Sec 1.1.3: grounding removal
    ///  • Eq (2.1.20): gradient if needed
    /// </summary>
    public sealed class FiniteElementSolver : ISolver
    {
        private readonly INumericSolver _numericSolver;

        public int N_phi { get; }
        public int L { get; }

        // Sub-block matrices
        public Complex[,] K { get; private set; }
        public Complex[,] M { get; private set; }
        public Complex[,] A_coup { get; private set; }
        public Complex[,] D { get; private set; }

        // Global system
        public Complex[,] SystemMatrix { get; private set; }
        public Complex[] SystemRHS { get; private set; }

        /// <summary>
        /// Initialize solver with mesh sizes and numeric solver.
        /// </summary>
        public FiniteElementSolver(FEMMesh mesh, INumericSolver numericSolver)
        {
            N_phi = mesh.Vertices.Count;
            L = mesh.GetElectrodes().Count;
            _numericSolver = numericSolver ?? throw new ArgumentNullException(nameof(numericSolver));

            // allocate
            K = new Complex[N_phi, N_phi];
            M = new Complex[N_phi, N_phi];
            A_coup = new Complex[N_phi, L];
            D = new Complex[L, L];
            SystemMatrix = new Complex[N_phi + L, N_phi + L];
            SystemRHS = new Complex[N_phi + L];
        }

        public PotentialDistribution SolveForward(IMesh mesh, BoundaryCondition boundaryCondition)
        {
            var femMesh = mesh as FEMMesh ?? throw new InvalidCastException();
            var bc = boundaryCondition as FEMBoundaryCondition ?? throw new InvalidCastException();
            var bcElectrodes = bc.GetElectrodes();

            return Solve(femMesh, bcElectrodes);
        }

        public PotentialDistribution SolveAdjoint(IMesh mesh, BoundaryCondition boundaryCondition, Complex[] adjointSource)
        {
            var femMesh = mesh as FEMMesh ?? throw new InvalidCastException();
            var bc = boundaryCondition as FEMBoundaryCondition ?? throw new InvalidCastException();
            var electrodes = femMesh.GetElectrodes();
            var bcElectrodes = bc.GetElectrodes();
            int bcElectrodeCount = bcElectrodes.Count();

            for (int i = 0; i < bcElectrodeCount; i++)
            {
                electrodes[i].Potential = 0.0;  
                bcElectrodes[i].Potential = 0.0;

                electrodes[i].Current = adjointSource[i].Real;  // TODO: add complex currents
                bcElectrodes[i].Current = adjointSource[i].Real;
            }

            return Solve(femMesh, bcElectrodes);
        }

        /// <summary>
        /// Solve forward CEM problem. First builds the saddle point system, applies grounding then solves with _numericSolver.
        /// Finally reinserts grounding and returing the arising potential distribution on the mesh.
        /// </summary>
        /// <param name="mesh"/> FEM mesh
        /// <param name="electrodes"/> electrode list with .Current, .IsGround set
        /// <returns>vector [alpha; U]</returns>
        private PotentialDistribution Solve(FEMMesh mesh, List<FEMElectrode> electrodes)
        {
            // Build sub-blocks of the Saddle-Point System
            BuildStiffnessMatrix(mesh);     // Eq (1.2.3)
            BuildRobinMassMatrix(mesh);     // Eq (1.1.12)
            BuildCouplingMatrix(mesh);      // Eq (1.1.12)
            BuildElectrodeMatrix(mesh);     // Eq (1.1.15)

            // Assemble saddle system S [α; U] = b  (Eq 1.1.16)
            BuildSystemMatrix();
            BuildRhsVector(electrodes);

            Debug.WriteLine("Assembled S and b");

            // Find ground index
            int groundId = electrodes.Find(e => e.IsGround)?.Id ?? throw new InvalidOperationException("No ground electrode.");

            // Remove ground DOF (Sec 1.1.3)
            var (Sg, bg) = ApplyGrounding(SystemMatrix, SystemRHS, groundId);

            // Solve reduced real system
            var solRed = _numericSolver.SolveLinearSystem(MatrixToReal(Sg), VectorToReal(bg));

            // Reconstruct full complex sol with U_ground=0
            var full = ReconstructFullSolution(solRed, groundId);

            Debug.WriteLine("Solution [α; U]:\n" + FormatComplexVector(full));

            // Create new potential distribution for the 
            Dictionary<int, double> pd = new();
            for (int i = 0; i < N_phi; i++)
                pd.Add(i, full[i].Real);

            var potentialDistribution = new PotentialDistribution(pd);

            // Set the mesh potentials
            mesh.SetPotentialDistribution(potentialDistribution);

            return potentialDistribution;
        }

        #region Assembly

        /// <summary>Eq (1.2.3)</summary>
        private void BuildStiffnessMatrix(FEMMesh mesh)
        {
            Array.Clear(K, 0, K.Length);
            ConductivityDistribution sigma = mesh.GetConductivityDistribution();
            var elements = mesh.GetElements().Cast<FEMElement>();

            foreach (var elem in elements)
            {
                double area = elem.Area;
                double sT = sigma.GetConductivity(elem.Id);
                // get shape gradients ∇φ^T (double[3,2]) from element, Eq (1.2.2)
                var grads = elem.GradPhi; // [3,2] array of (∂φ_i/∂x, ∂φ_i/∂y)
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        double gdot = grads[i, 0] * grads[j, 0] + grads[i, 1] * grads[j, 1];
                        K[elem.Vertices[i].GlobalId, elem.Vertices[j].GlobalId] += sT * area * gdot;
                    }
                }
            }
            //Debug.WriteLine("K:\n" + FormatComplexMatrix(K));
        }

        /// <summary>Eq (1.1.12)</summary>
        private void BuildRobinMassMatrix(FEMMesh mesh)
        {
            Array.Clear(M, 0, M.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();
            
            foreach (var el in electrodes)
            {
                double invZ = 1.0 / el.ZContact;
                double h = el.Length / el.VertexIds.Count;

                foreach (int vid in el.VertexIds)
                    M[vid, vid] += invZ * h;
            }
            //Debug.WriteLine("M:\n" + FormatComplexMatrix(M));
        }

        /// <summary>Eq (1.1.12)</summary>
        private void BuildCouplingMatrix(FEMMesh mesh)
        {
            Array.Clear(A_coup, 0, A_coup.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();

            foreach (var el in electrodes)
            {
                double invZ = 1.0 / el.ZContact;
                double h = el.Length / el.VertexIds.Count;

                foreach (int vid in el.VertexIds)
                    A_coup[vid, el.Id] += invZ * h;
            }
            //Debug.WriteLine("A_coup:\n" + FormatComplexMatrix(A_coup));
        }

        /// <summary>Eq (1.1.15)</summary>
        private void BuildElectrodeMatrix(FEMMesh mesh)
        {
            Array.Clear(D, 0, D.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();

            foreach (var el in electrodes)
                D[el.Id, el.Id] = el.Length / el.ZContact;

            //Debug.WriteLine("D:\n" + FormatComplexMatrix(D));
        }

        /// <summary>Eq (1.1.16)</summary>
        private void BuildSystemMatrix()
        {
            int N = N_phi, Lloc = L;
            Array.Clear(SystemMatrix, 0, SystemMatrix.Length);
            // K+M
            for (int i = 0; i < N; i++) for (int j = 0; j < N; j++)
                    SystemMatrix[i, j] = K[i, j] + M[i, j];
            // -A_coup
            for (int i = 0; i < N; i++) for (int ell = 0; ell < Lloc; ell++)
                    SystemMatrix[i, N + ell] = -A_coup[i, ell];

            for (int ell = 0; ell < Lloc; ell++) for (int i = 0; i < N; i++)
                    SystemMatrix[N + ell, i] = -A_coup[i, ell];
            // D
            for (int ell = 0; ell < Lloc; ell++)
                SystemMatrix[N + ell, N + ell] = D[ell, ell];
           // Debug.WriteLine("SystemMatrix:\n" + FormatComplexMatrix(SystemMatrix));
        }

        /// <summary>rhs = [0; I] </summary>
        private void BuildRhsVector(List<FEMElectrode> electrodes)
        {
            Array.Clear(SystemRHS, 0, SystemRHS.Length);

            for (int ell = 0; ell < L; ell++)
                SystemRHS[N_phi + ell] = electrodes[ell].Current;

           // Debug.WriteLine("SystemRHS:\n" + FormatComplexVector(SystemRHS));
        }
        #endregion

        #region Grounding

        /// <summary>Sec 1.1.3</summary>
        private (Complex[,], Complex[]) ApplyGrounding(Complex[,] A, Complex[] b, int groundId)
        {
            int full = N_phi + L;
            int rem = N_phi + groundId;
            int red = full - 1;
            var Ar = new Complex[red, red];
            var br = new Complex[red];
            for (int i = 0, ii = 0; i < full; i++)
            {
                if (i == rem) continue;
                br[ii] = b[i];
                for (int j = 0, jj = 0; j < full; j++)
                {
                    if (j == rem) continue;
                    Ar[ii, jj] = A[i, j]; jj++;
                }
                ii++;
            }
            //Debug.WriteLine($"Grounded size={red}\n" + FormatComplexMatrix(Ar));
            return (Ar, br);
        }

        /// <summary>reinsert U_ground=0</summary>
        private Complex[] ReconstructFullSolution(double[] solRed, int groundId)
        {
            int full = N_phi + L;
            var sol = new Complex[full];
            for (int i = 0, ir = 0; i < full; i++)
                sol[i] = i == N_phi + groundId ? Complex.Zero : new Complex(solRed[ir++], 0);
            return sol;
        }
        #endregion

        #region Utils
        /// <summary>
        /// Converts the provided complex 2D array to real 2D array by neglecting the complex parts.
        /// </summary>
        /// <param name="C">Complex matrix</param>
        /// <returns>Real valued matrix of same dimensions.</returns>
        private static double[,] MatrixToReal(Complex[,] C)
        {
            int r = C.GetLength(0), c = C.GetLength(1);
            var R = new double[r, c];
            for (int i = 0; i < r; i++) for (int j = 0; j < c; j++) R[i, j] = C[i, j].Real;
            return R;
        }

        /// <summary>
        /// Converts the provided complex array to real array by neglecting the complex parts.
        /// </summary>
        /// <param name="C">Complex array.</param>
        /// <returns>Real valued array of same dimension.</returns>
        private static double[] VectorToReal(Complex[] C)
        {
            int n = C.Length; var R = new double[n];
            for (int i = 0; i < n; i++) R[i] = C[i].Real;
            return R;
        }

        private static string FormatComplexMatrix(Complex[,] M)
        {
            var s = ""; int r = M.GetLength(0), c = M.GetLength(1);
            for (int i = 0; i < r; i++) { for (int j = 0; j < c; j++) s += M[i, j].ToString("0.###") + " "; s += "\n"; }
            return s;
        }
        private static string FormatComplexVector(Complex[] v)
        { var s = ""; for (int i = 0; i < v.Length; i++) s += v[i].ToString("0.###") + "\n"; return s; }
        #endregion
    }
}
