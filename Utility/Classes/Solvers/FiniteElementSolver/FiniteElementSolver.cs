using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;
using System.Linq;
using System.Threading.Tasks;
using Utility.Classes.Solvers;

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
        private readonly bool _useOmpParallelization;
        private readonly object[] _nodeLocks;
        private readonly object[] _electrodeLocks;

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
        public FiniteElementSolver(FEMMesh mesh, INumericSolver numericSolver, bool useOmpParallelization = false)
        {
            N_phi = mesh.Vertices.Count;
            L = mesh.GetElectrodes().Count;
            _numericSolver = numericSolver ?? throw new ArgumentNullException(nameof(numericSolver));
            _useOmpParallelization = useOmpParallelization;

            // allocate
            K = new Complex[N_phi, N_phi];
            M = new Complex[N_phi, N_phi];
            A_coup = new Complex[N_phi, L];
            D = new Complex[L, L];
            SystemMatrix = new Complex[N_phi + L, N_phi + L];
            SystemRHS = new Complex[N_phi + L];

            _nodeLocks = [.. Enumerable.Range(0, N_phi).Select(_ => new object())];
            _electrodeLocks = [.. Enumerable.Range(0, L).Select(_ => new object())];
        }

        /// <summary>
        /// Solves the forward CEM problem on the provided discretization by
        /// assembling the block saddle-point system and applying the configured
        /// numeric linear solver.
        /// </summary>
        /// <param name="discretization">Finite-element discretization.</param>
        /// <param name="boundaryCondition">Boundary condition carrying electrode drives.</param>
        /// <returns>Computed potential distribution.</returns>
        public PotentialDistribution SolveForward(IDiscretization discretization, BoundaryCondition boundaryCondition)
        {
            var femMesh = discretization as FEMMesh ?? throw new InvalidCastException();
            var bc = boundaryCondition as FEMBoundaryCondition ?? throw new InvalidCastException();
            var bcElectrodes = bc.GetElectrodes();

            return Solve(femMesh, bcElectrodes);
        }

        /// <summary>
        /// Reuses the forward assembly to solve the adjoint CEM problem driven
        /// by an electrode source vector.  This feeds reconstruction
        /// algorithms that require ∇·(σ∇λ) solves.
        /// </summary>
        /// <param name="discretization">Finite-element discretization.</param>
        /// <param name="boundaryCondition">Boundary condition providing electrode metadata.</param>
        /// <param name="adjointSource">Adjoint current density per electrode.</param>
        /// <returns>Adjoint potential distribution.</returns>
        public PotentialDistribution SolveAdjoint(IDiscretization discretization, BoundaryCondition boundaryCondition, Complex[] adjointSource)
        {
            var femMesh = discretization as FEMMesh ?? throw new InvalidCastException();
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
            if (_useOmpParallelization)
                AssembleSystemOmp(mesh, electrodes);
            else
                AssembleSystem(mesh, electrodes);

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

        private void AssembleSystem(FEMMesh mesh, List<FEMElectrode> electrodes)
        {
            BuildStiffnessMatrix(mesh);     // Eq (1.2.3)
            BuildRobinMassMatrix(mesh);     // Eq (1.1.12)
            BuildCouplingMatrix(mesh);      // Eq (1.1.12)
            BuildElectrodeMatrix(mesh);     // Eq (1.1.15)

            BuildSystemMatrix();            // Eq (1.1.16)
            BuildRhsVector(electrodes);
        }

        private void AssembleSystemOmp(FEMMesh mesh, List<FEMElectrode> electrodes)
        {
            BuildStiffnessMatrixOmp(mesh);     // Eq (1.2.3)
            BuildRobinMassMatrixOmp(mesh);     // Eq (1.1.12)
            BuildCouplingMatrixOmp(mesh);      // Eq (1.1.12)
            BuildElectrodeMatrixOmp(mesh);     // Eq (1.1.15)

            BuildSystemMatrixOmp();            // Eq (1.1.16)
            BuildRhsVectorOmp(electrodes);
        }

        /// <summary>
        /// Assembles the FEM stiffness matrix K = ∫ σ ∇φ_i · ∇φ_j using the
        /// conductivity stored on each element.
        /// </summary>
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
                var grads = elem.GradPhi; // [3][2] array of (∂φ_i/∂x, ∂φ_i/∂y)
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        double gdot = grads[i][0] * grads[j][0] + grads[i][1] * grads[j][1];
                        K[elem.Vertices[i].GlobalId, elem.Vertices[j].GlobalId] += sT * area * gdot;
                    }
                }
            }
            //Debug.WriteLine("K:\n" + FormatComplexMatrix(K));
        }

        private void BuildStiffnessMatrixOmp(FEMMesh mesh)
        {
            Array.Clear(K, 0, K.Length);
            ConductivityDistribution sigma = mesh.GetConductivityDistribution();
            var elements = mesh.GetElements().Cast<FEMElement>().ToList();

            Parallel.ForEach(elements, elem =>
            {
                var contributions = new (int row, int col, Complex value)[9];
                int idx = 0;
                double area = elem.Area;
                double sT = sigma.GetConductivity(elem.Id);
                var grads = elem.GradPhi;

                for (int i = 0; i < 3; i++)
                {
                    int row = elem.Vertices[i].GlobalId;
                    for (int j = 0; j < 3; j++)
                    {
                        int col = elem.Vertices[j].GlobalId;
                        double gdot = grads[i][0] * grads[j][0] + grads[i][1] * grads[j][1];
                        contributions[idx++] = (row, col, new Complex(sT * area * gdot, 0));
                    }
                }

                for (int k = 0; k < idx; k++)
                {
                    var (row, col, value) = contributions[k];
                    lock (_nodeLocks[row])
                    {
                        K[row, col] += value;
                    }
                }
            });
        }

        /// <summary>
        /// Integrates the boundary impedance contributions from the complete
        /// electrode model to form the Robin mass matrix M.
        /// </summary>
        private void BuildRobinMassMatrix(FEMMesh mesh)
        {
            Array.Clear(M, 0, M.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();

            foreach (var el in electrodes)
            {
                if (el.ZContact <= 0.0)
                    continue;

                var contactVertexIds = GetContactVertexIds(mesh, el);
                double invZ = 1.0 / el.ZContact;
                if (!el.PointElectrode && contactVertexIds.Count >= 2)
                {
                    var segments = BuildElectrodeSegments(mesh, contactVertexIds);
                    double totalLength = 0.0;
                    foreach (var (a, b, len) in segments)
                    {
                        if (len <= 0.0)
                            continue;

                        totalLength += len;
                        var diag = new Complex(invZ * len / 3.0, 0.0);
                        var off = new Complex(invZ * len / 6.0, 0.0);
                        M[a, a] += diag;
                        M[b, b] += diag;
                        M[a, b] += off;
                        M[b, a] += off;
                    }

                    if (totalLength > 0.0)
                    {
                        el.Length = totalLength;
                        continue;
                    }
                }

                double length = ResolveElectrodeLength(mesh, el, contactVertexIds);
                int count = contactVertexIds.Count;
                var diagValue = new Complex(invZ * (length / count), 0.0);
                foreach (int vid in contactVertexIds)
                    M[vid, vid] += diagValue;
            }
            //Debug.WriteLine("M:\n" + FormatComplexMatrix(M));
        }

        private void BuildRobinMassMatrixOmp(FEMMesh mesh)
        {
            Array.Clear(M, 0, M.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            Parallel.ForEach(electrodes, el =>
            {
                if (el.ZContact <= 0.0)
                    return;

                var contactVertexIds = GetContactVertexIds(mesh, el);
                double invZ = 1.0 / el.ZContact;

                if (!el.PointElectrode && contactVertexIds.Count >= 2)
                {
                    var segments = BuildElectrodeSegments(mesh, contactVertexIds);
                    double totalLength = 0.0;
                    foreach (var (a, b, len) in segments)
                    {
                        if (len <= 0.0)
                            continue;

                        totalLength += len;
                        var diag = new Complex(invZ * len / 3.0, 0.0);
                        var off = new Complex(invZ * len / 6.0, 0.0);
                        AddToNodeMatrixThreadSafe(M, a, a, diag);
                        AddToNodeMatrixThreadSafe(M, b, b, diag);
                        AddToNodeMatrixThreadSafe(M, a, b, off);
                        AddToNodeMatrixThreadSafe(M, b, a, off);
                    }

                    if (totalLength > 0.0)
                    {
                        el.Length = totalLength;
                        return;
                    }
                }

                double length = ResolveElectrodeLength(mesh, el, contactVertexIds);
                int count = contactVertexIds.Count;
                var diagValue = new Complex(invZ * (length / count), 0.0);
                foreach (int vid in contactVertexIds)
                    AddToNodeMatrixThreadSafe(M, vid, vid, diagValue);
            });
        }

        /// <summary>
        /// Builds the coupling matrix that links node potentials with
        /// electrode potentials through the contact impedance terms.
        /// </summary>
        private void BuildCouplingMatrix(FEMMesh mesh)
        {
            Array.Clear(A_coup, 0, A_coup.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();

            foreach (var el in electrodes)
            {
                if (el.ZContact <= 0.0)
                    continue;

                var contactVertexIds = GetContactVertexIds(mesh, el);
                double invZ = 1.0 / el.ZContact;
                if (!el.PointElectrode && contactVertexIds.Count >= 2)
                {
                    var segments = BuildElectrodeSegments(mesh, contactVertexIds);
                    double totalLength = 0.0;
                    foreach (var (a, b, len) in segments)
                    {
                        if (len <= 0.0)
                            continue;

                        totalLength += len;
                        var value = new Complex(invZ * len / 2.0, 0.0);
                        A_coup[a, el.Id] += value;
                        A_coup[b, el.Id] += value;
                    }

                    if (totalLength > 0.0)
                    {
                        el.Length = totalLength;
                        continue;
                    }
                }

                double length = ResolveElectrodeLength(mesh, el, contactVertexIds);
                var valuePoint = new Complex(invZ * (length / contactVertexIds.Count), 0.0);
                foreach (int vid in contactVertexIds)
                    A_coup[vid, el.Id] += valuePoint;
            }
            //Debug.WriteLine("A_coup:\n" + FormatComplexMatrix(A_coup));
        }

        private void BuildCouplingMatrixOmp(FEMMesh mesh)
        {
            Array.Clear(A_coup, 0, A_coup.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            Parallel.ForEach(electrodes, el =>
            {
                if (el.ZContact <= 0.0)
                    return;

                var contactVertexIds = GetContactVertexIds(mesh, el);
                double invZ = 1.0 / el.ZContact;
                if (!el.PointElectrode && contactVertexIds.Count >= 2)
                {
                    var segments = BuildElectrodeSegments(mesh, contactVertexIds);
                    double totalLength = 0.0;
                    foreach (var (a, b, len) in segments)
                    {
                        if (len <= 0.0)
                            continue;

                        totalLength += len;
                        var value = new Complex(invZ * len / 2.0, 0.0);
                        AddToCouplingMatrixThreadSafe(a, el.Id, value);
                        AddToCouplingMatrixThreadSafe(b, el.Id, value);
                    }

                    if (totalLength > 0.0)
                    {
                        el.Length = totalLength;
                        return;
                    }
                }

                double length = ResolveElectrodeLength(mesh, el, contactVertexIds);
                var valuePoint = new Complex(invZ * (length / contactVertexIds.Count), 0.0);
                foreach (int vid in contactVertexIds)
                    AddToCouplingMatrixThreadSafe(vid, el.Id, valuePoint);
            });
        }

        /// <summary>
        /// Populates the diagonal electrode matrix D that stores the
        /// aggregate contact admittance for each electrode pad.
        /// </summary>
        private void BuildElectrodeMatrix(FEMMesh mesh)
        {
            Array.Clear(D, 0, D.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>();

            foreach (var el in electrodes)
            {
                if (el.ZContact <= 0.0)
                {
                    D[el.Id, el.Id] = 0.0;
                    continue;
                }

                var contactVertexIds = GetContactVertexIds(mesh, el);
                double invZ = 1.0 / el.ZContact;
                if (!el.PointElectrode && contactVertexIds.Count >= 2)
                {
                    double total = 0.0;
                    foreach (var segment in BuildElectrodeSegments(mesh, contactVertexIds))
                        total += segment.Length;

                    if (total <= 0.0)
                    {
                        double lengthFallback = ResolveElectrodeLength(mesh, el, contactVertexIds);
                        D[el.Id, el.Id] = lengthFallback * invZ;
                    }
                    else
                    {
                        el.Length = total;
                        D[el.Id, el.Id] = total * invZ;
                    }
                }
                else
                {
                    double length = ResolveElectrodeLength(mesh, el, contactVertexIds);
                    D[el.Id, el.Id] = length * invZ;
                }
            }

            //Debug.WriteLine("D:\n" + FormatComplexMatrix(D));
        }

        private void BuildElectrodeMatrixOmp(FEMMesh mesh)
        {
            Array.Clear(D, 0, D.Length);
            var electrodes = mesh.GetElectrodes().Cast<FEMElectrode>().ToList();

            Parallel.ForEach(electrodes, el =>
            {
                if (el.ZContact <= 0.0)
                    return;

                var contactVertexIds = GetContactVertexIds(mesh, el);
                double invZ = 1.0 / el.ZContact;
                if (!el.PointElectrode && contactVertexIds.Count >= 2)
                {
                    double total = 0.0;
                    foreach (var segment in BuildElectrodeSegments(mesh, contactVertexIds))
                        total += segment.Length;

                    if (total <= 0.0)
                    {
                        double lengthFallback = ResolveElectrodeLength(mesh, el, contactVertexIds);
                        AddToElectrodeMatrixThreadSafe(el.Id, new Complex(lengthFallback * invZ, 0.0));
                    }
                    else
                    {
                        el.Length = total;
                        AddToElectrodeMatrixThreadSafe(el.Id, new Complex(total * invZ, 0.0));
                    }
                }
                else
                {
                    double length = ResolveElectrodeLength(mesh, el, contactVertexIds);
                    AddToElectrodeMatrixThreadSafe(el.Id, new Complex(length * invZ, 0.0));
                }
            });
        }

        private List<(int StartId, int EndId, double Length)> BuildElectrodeSegments(FEMMesh mesh, List<int> orderedVertexIds)
        {
            var segments = new List<(int, int, double)>();
            if (orderedVertexIds == null || orderedVertexIds.Count < 2)
                return segments;

            for (int i = 0; i < orderedVertexIds.Count - 1; i++)
            {
                var start = mesh.GetVertexById(orderedVertexIds[i]);
                var end = mesh.GetVertexById(orderedVertexIds[i + 1]);
                double dx = start.X - end.X;
                double dy = start.Y - end.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 0.0)
                    segments.Add((start.GlobalId, end.GlobalId, length));
            }
            return segments;
        }

        /// <summary>
        /// Assembles the saddle-point block system combining K, M, A_coup, and
        /// D in the canonical CEM ordering.
        /// </summary>
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

        private void BuildSystemMatrixOmp()
        {
            int N = N_phi, Lloc = L;
            Array.Clear(SystemMatrix, 0, SystemMatrix.Length);

            Parallel.For(0, N, i =>
            {
                for (int j = 0; j < N; j++)
                    SystemMatrix[i, j] = K[i, j] + M[i, j];

                for (int ell = 0; ell < Lloc; ell++)
                    SystemMatrix[i, N + ell] = -A_coup[i, ell];
            });

            Parallel.For(0, Lloc, ell =>
            {
                for (int i = 0; i < N; i++)
                    SystemMatrix[N + ell, i] = -A_coup[i, ell];

                SystemMatrix[N + ell, N + ell] = D[ell, ell];
            });
        }

        /// <summary>
        /// Builds the right-hand side vector with nodal entries set to zero
        /// and electrode entries equal to the prescribed currents.
        /// </summary>
        private void BuildRhsVector(List<FEMElectrode> electrodes)
        {
            Array.Clear(SystemRHS, 0, SystemRHS.Length);

            for (int ell = 0; ell < L; ell++)
                SystemRHS[N_phi + ell] = electrodes[ell].Current;

           // Debug.WriteLine("SystemRHS:\n" + FormatComplexVector(SystemRHS));
        }

        private void BuildRhsVectorOmp(List<FEMElectrode> electrodes)
        {
            Array.Clear(SystemRHS, 0, SystemRHS.Length);

            Parallel.For(0, L, ell =>
            {
                SystemRHS[N_phi + ell] = electrodes[ell].Current;
            });
        }
        #endregion

        #region Grounding

        /// <summary>
        /// Removes the ground electrode degree of freedom to obtain a full-rank
        /// linear system as described in Sec. 1.1.3.
        /// </summary>
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

        /// <summary>
        /// Reconstructs the full solution vector by reinserting the grounded
        /// electrode with zero potential.
        /// </summary>
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
        private List<int> GetContactVertexIds(FEMMesh mesh, FEMElectrode electrode)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (electrode == null)
                throw new ArgumentNullException(nameof(electrode));

            List<int> ids;
            if (electrode.FEMVertexIds != null && electrode.FEMVertexIds.Count > 0)
            {
                ids = mesh.OrderVerticesAlongBoundary(electrode.FEMVertexIds);
            }
            else if (electrode.MeshId >= 0)
            {
                ids = new List<int> { electrode.MeshId };
            }
            else
            {
                throw new InvalidOperationException($"Electrode {electrode.Id} does not reference any FEM vertex.");
            }

            if (ids.Count == 0)
                throw new InvalidOperationException($"Electrode {electrode.Id} does not reference any FEM vertex.");

            var unique = new List<int>(ids.Count);
            var seen = new HashSet<int>();
            foreach (int id in ids)
            {
                if (id < 0 || id >= N_phi)
                    throw new InvalidOperationException($"Electrode {electrode.Id} references missing FEM vertex {id}.");

                if (seen.Add(id))
                    unique.Add(id);
            }

            if (unique.Count == 0)
                throw new InvalidOperationException($"Electrode {electrode.Id} does not reference any FEM vertex.");

            return unique;
        }

        private double ResolveElectrodeLength(FEMMesh mesh, FEMElectrode electrode, List<int> contactVertexIds)
        {
            double length = electrode.Length;
            if (length > 0.0)
                return length;

            if (contactVertexIds != null && contactVertexIds.Count > 0)
                length = mesh.ComputeElectrodeLength(contactVertexIds);

            if ((length <= 0.0 || double.IsNaN(length)) && electrode.MeshId >= 0)
                length = mesh.ComputeElectrodeLength(new List<int> { electrode.MeshId });

            if (length <= 0.0 || double.IsNaN(length))
                length = ComputeAverageBoundarySpacing(mesh);

            if (length <= 0.0 || double.IsNaN(length))
                length = 1e-6;

            electrode.Length = length;
            return length;
        }

        private static double ComputeAverageBoundarySpacing(FEMMesh mesh)
        {
            var boundary = mesh.GetOrderedBoundaryVertices();
            int count = boundary.Count;
            if (count < 2)
                return 0.0;

            double total = 0.0;
            for (int i = 0; i < count; i++)
            {
                var a = boundary[i];
                var b = boundary[(i + 1) % count];
                double dx = a.X - b.X;
                double dy = a.Y - b.Y;
                total += Math.Sqrt(dx * dx + dy * dy);
            }

            return total / count;
        }

        private void AddToNodeMatrixThreadSafe(Complex[,] matrix, int row, int col, Complex value)
        {
            if (row == col)
            {
                lock (_nodeLocks[row])
                    matrix[row, col] += value;
                return;
            }

            int first = Math.Min(row, col);
            int second = Math.Max(row, col);
            lock (_nodeLocks[first])
            {
                lock (_nodeLocks[second])
                {
                    matrix[row, col] += value;
                }
            }
        }

        private void AddToCouplingMatrixThreadSafe(int nodeId, int electrodeId, Complex value)
        {
            lock (_nodeLocks[nodeId])
                A_coup[nodeId, electrodeId] += value;
        }

        private void AddToElectrodeMatrixThreadSafe(int electrodeId, Complex value)
        {
            lock (_electrodeLocks[electrodeId])
                D[electrodeId, electrodeId] += value;
        }

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

        /// <summary>
        /// Formats a complex matrix for debugging output using a compact string representation.
        /// </summary>
        private static string FormatComplexMatrix(Complex[,] M)
        {
            var s = ""; int r = M.GetLength(0), c = M.GetLength(1);
            for (int i = 0; i < r; i++) { for (int j = 0; j < c; j++) s += M[i, j].ToString("0.###") + " "; s += "\n"; }
            return s;
        }
        /// <summary>
        /// Formats a complex vector for debugging output.
        /// </summary>
        private static string FormatComplexVector(Complex[] v)
        { var s = ""; for (int i = 0; i < v.Length; i++) s += v[i].ToString("0.###") + "\n"; return s; }
        #endregion
    }
}