using Utility.Classes.Measurement;

namespace Utility.Classes.Reconstruction.Convexification
{
    /// <summary>
    /// Electrode-level transformed data for a single drive-pattern frame.
    /// The convexification pipeline operates directly on these finite-electrode
    /// quantities instead of introducing synthetic continuum traces.
    /// </summary>
    public sealed class ConvexificationBoundaryData
    {
        /// <summary>The requested step index supplied by the caller.</summary>
        public int RequestedStepIndex { get; init; }

        /// <summary>Step index normalised to the available drive-pattern cycle.</summary>
        public int NormalizedStepIndex { get; init; }

        /// <summary>Measurement-pattern metadata for the frame when available.</summary>
        public MeasurementPatternStep? PatternStep { get; init; }

        /// <summary>Raw measured electrode data before completion/interpolation.</summary>
        public double[] RawFrame { get; init; } = [];

        /// <summary>Completed electrode voltages U_l(t) in physical electrode order.</summary>
        public double[] ElectrodeVoltages { get; init; } = [];

        /// <summary>Known drive currents I_l(t) reconstructed from the drive pattern.</summary>
        public double[] DriveCurrents { get; init; } = [];

        /// <summary>Electrode lengths used in the raw proxy formulas.</summary>
        public double[] ElectrodeLengths { get; init; } = [];

        /// <summary>Electrode contact impedances used in the raw proxy formulas.</summary>
        public double[] ContactImpedances { get; init; } = [];

        /// <summary>Positivity shift c(t) applied to the raw voltages.</summary>
        public double PositivityShift { get; set; }

        /// <summary>Boundary proxy g0_l(t) = Ubar_l(t) - z_l I_l(t) / |E_l|.</summary>
        public double[] G0 { get; set; } = [];

        /// <summary>Boundary proxy g1_l(t) = I_l(t) / |E_l|.</summary>
        public double[] G1 { get; set; } = [];

        /// <summary>Logarithmic boundary data s0_l(t) = log(g0_l(t)).</summary>
        public double[] S0 { get; set; } = [];

        /// <summary>Flux-normalised boundary data s1_l(t) = g1_l(t) / g0_l(t).</summary>
        public double[] S1 { get; set; } = [];

        /// <summary>Drive derivative b0_l(t) = d_t s0_l(t).</summary>
        public double[] B0 { get; set; } = [];

        /// <summary>Drive derivative c0_l(t) = d_t s1_l(t).</summary>
        public double[] C0 { get; set; } = [];

        /// <summary>Closed-system Dirichlet proxy bEps_l(t) = b0_l(t) - epsilon s0_l(t).</summary>
        public double[] BEpsilon { get; set; } = [];

        /// <summary>Closed-system Neumann proxy cEps_l(t) = c0_l(t) - epsilon s1_l(t).</summary>
        public double[] CEpsilon { get; set; } = [];
    }
}
