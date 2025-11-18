using System;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// Centralised helper for mapping between physical SI units and lattice units (LU).
    /// The numerics run with Δx_LU = Δt_LU = 1, while callers may provide inputs in SI.
    /// </summary>
    internal static class LBUnitConverter
    {
        /// <summary>
        /// Physical domain length along the X direction [m].  Used together with <see cref="Nx"/> to
        /// derive the physical lattice spacing Δx_phys.
        /// </summary>
        public static double LxPhys { get; private set; } = 1.0;

        /// <summary>
        /// Number of lattice nodes along X.  The physical spacing follows Δx_phys = LxPhys / (Nx - 1).
        /// </summary>
        public static int Nx { get; private set; } = 1;

        /// <summary>
        /// Reference conductivity in physical units [S/m].  Stored for completeness so downstream code
        /// can reason about relative scaling, e.g. for relaxation-time clamping.
        /// </summary>
        public static double SigmaRefPhys { get; private set; } = 1.0;

        /// <summary>
        /// Physical time step [s].
        /// </summary>
        public static double DeltaTPhys { get; private set; } = 1.0;

        /// <summary>
        /// True when conductivity and electrode currents are provided in SI units and must be converted
        /// before entering the lattice Boltzmann numerics.
        /// </summary>
        public static bool InputsArePhysical { get; private set; }
            = false;

        /// <summary>
        /// Derived physical lattice spacing [m].  Returns zero-safe value when Nx == 1.
        /// </summary>
        public static double DeltaXPhys => LxPhys / Math.Max(1, Nx - 1);

        /// <summary>
        /// Lattice spacing in LU.  Numerics are implemented with Δx_LU = 1.
        /// </summary>
        public const double DeltaX_LU = 1.0;

        /// <summary>
        /// Lattice time step in LU.  Numerics are implemented with Δt_LU = 1.
        /// </summary>
        public const double DeltaT_LU = 1.0;

        /// <summary>
        /// Configures the mapping between physical and lattice units.  Call this once at application start
        /// (or whenever the discretisation changes) before invoking the solver.
        /// </summary>
        public static void Configure(double lxPhys, int nx, double sigmaRefPhys, double deltaTPhys, bool inputsArePhysical)
        {
            if (lxPhys <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(lxPhys), "Physical domain length must be positive.");
            if (nx < 1)
                throw new ArgumentOutOfRangeException(nameof(nx), "Number of lattice nodes must be positive.");
            if (deltaTPhys <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(deltaTPhys), "Physical time step must be positive.");

            LxPhys = lxPhys;
            Nx = nx;
            SigmaRefPhys = sigmaRefPhys;
            DeltaTPhys = deltaTPhys;
            InputsArePhysical = inputsArePhysical;
        }

        /// <summary>
        /// Conductivity (diffusivity) mapping using relation (a) from Krüger et al., "Non-dimensionalisation
        /// and choice of parameters": D_LU = D_phys * (Δt_phys / Δx_phys^2).
        /// </summary>
        public static double ConductivityPhysToLU(double sigmaPhys)
            => sigmaPhys * (DeltaTPhys / (DeltaXPhys * DeltaXPhys));

        /// <summary>
        /// Flux density mapping using relation (b): enforce equality of (j_n Δx)/σ between physical and lattice
        /// units (Gebäck &amp; Heintz for the Neumann boundary).  With σ_LU = σ_phys (Δt_phys / Δx_phys^2) this yields
        /// j_n^LU = j_n^phys (Δt_phys / Δx_phys).
        /// </summary>
        public static double FluxDensityPhysToLU(double jPhys)
            => jPhys * (DeltaTPhys / DeltaXPhys);

    }
}
