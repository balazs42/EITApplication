using System;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver
{
    /// <summary>
    /// Centralised helper for mapping between physical SI units and lattice units (LU).
    /// The numerics run with Δx_LU = Δt_LU = 1 and all conversions are performed here so the
    /// solver/kernels manipulate only LU values.
    /// </summary>
    internal static class LBUnitConverter
    {
        /// <summary>
        /// Physical lattice spacing Δx expressed in metres.  Numerics run with Δx_LU = 1 and use this
        /// value solely for unit conversions.
        /// </summary>
        public static double DeltaXPhys { get; private set; } = 1.0;

        /// <summary>
        /// Physical time step [s].  The solver always advances with Δt_LU = 1 and relies on this scale
        /// factor when mapping conductivities and flux densities between SI and LU.
        /// </summary>
        public static double DeltaTPhys { get; private set; } = 1.0;

        /// <summary>
        /// True when caller-supplied conductivities/currents live in SI.  When <c>false</c>, values are
        /// assumed to already be expressed in lattice units and no conversion is applied.
        /// </summary>
        public static bool InputsArePhysical { get; private set; }
            = false;

        /// <summary>
        /// Configures the mapping between SI and LU.  Call once whenever the discretisation metrics
        /// change so that all downstream computations stay consistent.
        /// </summary>
        /// <param name="deltaXPhys">Physical lattice spacing Δx [m].</param>
        /// <param name="deltaTPhys">Physical time step Δt [s].</param>
        /// <param name="inputsArePhysical">Whether solver inputs are specified in SI.</param>
        public static void Configure(double deltaXPhys, double deltaTPhys, bool inputsArePhysical)
        {
            if (deltaXPhys <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(deltaXPhys), "Physical Δx must be positive.");
            if (deltaTPhys <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(deltaTPhys), "Physical Δt must be positive.");

            DeltaXPhys = deltaXPhys;
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
        /// units (Gebäck &amp; Heintz).  With σ_LU = σ_phys (Δt_phys / Δx_phys^2) this yields j_LU = j_phys (Δt_phys / Δx_phys).
        /// </summary>
        public static double FluxDensityPhysToLU(double jPhys)
            => jPhys * (DeltaTPhys / DeltaXPhys);

        /// <summary>
        /// Converts flux density from lattice units back to SI.  This is primarily used in debug-only
        /// current-closure checks where the solver validates that Σ (j_n Δs) matches the prescribed
        /// electrode current in the original unit system.
        /// </summary>
        public static double FluxDensityLUToPhys(double jLu)
            => jLu * (DeltaXPhys / DeltaTPhys);

    }
}
