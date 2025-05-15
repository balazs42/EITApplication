namespace Utility.Classes.Solvers
{
    public static class LatticeBoltzmannSolver
    {
        private static int Nx = 50;
        private static int Ny = 50;


        private static readonly (int cx, int cy)[] c =
        {
            ( 0,  0), ( 1,  0), ( 0,  1), (-1,  0), ( 0, -1),
            ( 1,  1), (-1,  1), (-1,-1), ( 1, -1)
        };

        // index of opposite direction 0↔0, 1↔3, 2↔4, 5↔7, 6↔8
        private static readonly int[] opp = { 0, 3, 4, 1, 2, 7, 8, 5, 6 };


        private static readonly double[] w =
        {
            4.0 / 9.0,  1.0 / 9.0, 1.0 / 9.0, 1.0 / 9.0, 1.0 / 9.0,
            1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0, 1.0 / 36.0
        };


        // -------- Dirichlet (constant‑potential) nodes -----------------------------
        private static bool[,] isPinned = new bool[Nx, Ny];
        private static double[,] pinValue = new double[Nx, Ny];
        public static void PinNode(int x, int y, double value)
        {
            isPinned[x, y] = true;
            pinValue[x, y] = value;
        }

        // ==========================================================================
        // INTERNAL OBSTACLE (bounce‑back wall)
        // ==========================================================================
        private static bool[,] isSolid = new bool[Nx, Ny];


        // --- relaxation time (pick τ > 0.5 for stability) --------------------
        private const double tau = 1.0;            // ⇒ D = (tau‑0.5)/3 = 1/6
        private const double omega = 1.0 / tau;    // BGK collision rate

        // --- distribution functions g[i,j,k] ---------------------------------
        private static double[,,] g = new double[Nx, Ny, 9];
        private static double[,,] gNext = new double[Nx, Ny, 9];

        // --- macroscopic scalar field ----------------------------------------
        private static double[,] phi = new double[Nx, Ny];

        // ---------------------------------------------------------------------
        // INITIALISATION
        // ---------------------------------------------------------------------
        /// <paramname="init">User routine giving the initial φ value at (x,y)
        public static void Initialize(Func<int, int, double> init)
        {
            for (int x = 0; x < Nx; ++x)
                for (int y = 0; y < Ny; ++y)
                {
                    phi[x, y] = init(x, y);

                    // local equilibrium: g_eq = w_i · φ  (zero velocity)
                    for (int k = 0; k < 9; ++k)
                        g[x, y, k] = w[k] * phi[x, y];
                }
        }

        public static void Configure(int nx, int ny)
        {
            Nx = nx; Ny = ny;

            isPinned = new bool[Nx, Ny];
            pinValue = new double[Nx, Ny];
            isSolid = new bool[Nx, Ny];
            g = new double[Nx, Ny, 9];
            gNext = new double[Nx, Ny, 9];
            phi = new double[Nx, Ny];
        }

        public static void Reset() => Configure(Nx, Ny);


        // Pin every 5‑th boundary node to the given potential (default 1.0)
        public static void PinEveryFifthBoundaryNode(double value = 1.0)
        {
            int counter = 0;

            // ---- bottom (y = 0) ----------------------------------------------------
            for (int x = 0; x < Nx; ++x)
                if (counter++ % 5 == 0) PinNode(x, 0, value);

            // ---- right side (x = Nx‑1, skip the already done corner) --------------
            for (int y = 1; y < Ny; ++y)
                if (counter++ % 5 == 0) PinNode(Nx - 1, y, value);

            // ---- top (y = Ny‑1, reversed x, skip corner) --------------------------
            for (int x = Nx - 2; x >= 0; --x)
                if (counter++ % 5 == 0) PinNode(x, Ny - 1, value);

            // ---- left side (x = 0, reversed y, skip both corners) -----------------
            for (int y = Ny - 2; y >= 1; --y)
                if (counter++ % 5 == 0) PinNode(0, y, value);
        }

        // Put a square obstacle in the centre covering 1/5 of the domain
        public static void AddCentralSquareObstacle()
        {
            int side = Math.Min(Nx, Ny) / 5;          // 1/5 of lattice length
            int x0 = Nx / 2 - side / 2;
            int y0 = Ny / 2 - side / 2;

            for (int x = x0; x < x0 + side; ++x)
                for (int y = y0; y < y0 + side; ++y)
                    isSolid[x, y] = true;
        }

        // ---------------------------------------------------------------------
        // ONE LBM TIME STEP
        // ---------------------------------------------------------------------
        private static void Step()
        {
            // -------- Collision + macro update --------------------------------
            for (int x = 0; x < Nx; ++x)
                for (int y = 0; y < Ny; ++y)
                {
                    if (isSolid[x, y]) continue;    //  ignore obstacle

                    double localPhi = 0.0;
                    for (int k = 0; k < 9; ++k) localPhi += g[x, y, k];
                    phi[x, y] = localPhi;

                    for (int k = 0; k < 9; ++k)
                    {
                        double geq = w[k] * localPhi;
                        g[x, y, k] += -omega * (g[x, y, k] - geq);        // BGK
                    }
                }

            // -------- Streaming (periodic boundary) ---------------------------
            //for (int x = 0; x < Nx; ++x)
            //for (int y = 0; y < Ny; ++y)
            //for (int k = 0; k < 9; ++k)
            //{
            //    int xn = (x + c[k].cx + Nx) % Nx;
            //    int yn = (y + c[k].cy + Ny) % Ny;
            //    gNext[xn, yn, k] = g[x, y, k];
            //}

            // -------- Streaming with bounce‑back ---------------------------------------
            for (int x = 0; x < Nx; ++x)
                for (int y = 0; y < Ny; ++y)
                {
                    if (isSolid[x, y]) continue;

                    for (int k = 0; k < 9; ++k)
                    {
                        int xn = x + c[k].cx;
                        int yn = y + c[k].cy;

                        // Is the neighbour inside the domain?
                        if (xn >= 0 && xn < Nx && yn >= 0 && yn < Ny)
                        {
                            // standard streaming to interior cell
                            gNext[xn, yn, k] = g[x, y, k];
                        }
                        else
                        {
                            // hit a wall: reflect back into the same node, opposite direction
                            gNext[x, y, opp[k]] = g[x, y, k];
                        }
                    }
                }

            // swap distribution buffers
            Array.Copy(gNext, g, gNext.Length);

            // -------- Enforce constant‑potential pins ----------------------------------
            for (int x = 0; x < Nx; ++x)
                for (int y = 0; y < Ny; ++y)
                {
                    if (!isPinned[x, y]) continue;
                    double val = pinValue[x, y];
                    phi[x, y] = val;
                    for (int k = 0; k < 9; ++k) g[x, y, k] = w[k] * val;   // overwrite df's
                }
        }

        public static void SetRelaxationTime(int x, int y, double tauXY) 
        { 
        
        }


        // ---------------------------------------------------------------------
        // DRIVER
        // ---------------------------------------------------------------------

        // Run nSteps LBM iterations and return the scalar field φ.        
        public static double[,] Run(int nSteps)
        {
            for (int t = 0; t < nSteps; ++t) Step();
            return phi;
        }
    }
}
