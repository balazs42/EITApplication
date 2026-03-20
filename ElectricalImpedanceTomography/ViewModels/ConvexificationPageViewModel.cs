using System.ComponentModel;
using System.Runtime.CompilerServices;
using ServiceLayer;
using Utility.Classes.Application;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction.Convexification;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    /// <summary>
    /// Specialised reconstruction view model that reuses the existing
    /// reconstruction workflow UI but routes all classic reconstruction actions
    /// through the convexification service.
    /// </summary>
    public class ConvexificationPageViewModel : ReconstructionPageViewModel
    {
        private ConvexificationOptions Options => ReconstructionParameters.ConvexificationOptions;

        public ConvexificationPageViewModel(IConvexificationReconstructionService convexificationReconstructionService,
                                            IBlockFemReconstructionService blockReconstructionService,
                                            IReconstructionExportService exportService)
            : base(convexificationReconstructionService, blockReconstructionService, exportService)
        {
            ApplyConvexificationDefaults();
            PropertyChanged += OnConvexificationPageViewModelPropertyChanged;
        }

        /// <summary>
        /// Carleman phase strength lambda used in exp(2 lambda omega.x).
        /// </summary>
        public double ConvexificationLambda
        {
            get => Options.Lambda;
            set => SetDoubleOption(value, Options.Lambda, v => Options.Lambda = v);
        }

        /// <summary>
        /// Relative weight of the interior Carleman residual compared with the
        /// boundary penalties.
        /// </summary>
        public double ConvexificationInteriorResidualWeight
        {
            get => Options.InteriorResidualWeight;
            set => SetDoubleOption(value, Options.InteriorResidualWeight, v => Options.InteriorResidualWeight = v);
        }

        /// <summary>
        /// Stabilization weight used in the Carleman residual regularization term.
        /// </summary>
        public double ConvexificationBeta
        {
            get => Options.Beta;
            set => SetDoubleOption(value, Options.Beta, v => Options.Beta = v);
        }

        /// <summary>
        /// Closure parameter epsilon in s = r - epsilon w.
        /// </summary>
        public double ConvexificationEpsilon
        {
            get => Options.Epsilon;
            set => SetDoubleOption(value, Options.Epsilon, v => Options.Epsilon = v);
        }

        /// <summary>
        /// Requested positivity floor d0 used by the shifted raw boundary proxy.
        /// </summary>
        public double ConvexificationD0
        {
            get => Options.D0;
            set => SetDoubleOption(value, Options.D0, v => Options.D0 = v);
        }

        /// <summary>
        /// Extra safety margin added after positivity enforcement.
        /// </summary>
        public double ConvexificationPositivityMargin
        {
            get => Options.PositivityMargin;
            set => SetDoubleOption(value, Options.PositivityMargin, v => Options.PositivityMargin = v);
        }

        /// <summary>
        /// Dirichlet penalty weight used in the electrode-wise boundary loss.
        /// </summary>
        public double ConvexificationBoundaryDirichletWeight
        {
            get => Options.BoundaryDirichletWeight;
            set => SetDoubleOption(value, Options.BoundaryDirichletWeight, v => Options.BoundaryDirichletWeight = v);
        }

        /// <summary>
        /// Neumann penalty weight used in the electrode-wise boundary loss.
        /// </summary>
        public double ConvexificationBoundaryNeumannWeight
        {
            get => Options.BoundaryNeumannWeight;
            set => SetDoubleOption(value, Options.BoundaryNeumannWeight, v => Options.BoundaryNeumannWeight = v);
        }

        /// <summary>
        /// Damping factor used when blending each inner descent update.
        /// </summary>
        public double ConvexificationStepSize
        {
            get => Options.StepSize;
            set => SetDoubleOption(value, Options.StepSize, v => Options.StepSize = v);
        }

        /// <summary>
        /// Maximum number of inner least-squares iterations performed per cycle.
        /// </summary>
        public int ConvexificationInnerIterations
        {
            get => Options.MaxIterations;
            set => SetIntOption(Math.Max(1, value), Options.MaxIterations, v => Options.MaxIterations = v);
        }

        /// <summary>
        /// Relative objective tolerance for the inner convexification solve.
        /// </summary>
        public double ConvexificationTolerance
        {
            get => Options.Tolerance;
            set => SetDoubleOption(value, Options.Tolerance, v => Options.Tolerance = v);
        }

        /// <summary>
        /// Stationarity threshold for the preconditioned inner descent field.
        /// </summary>
        public double ConvexificationInnerGradientTolerance
        {
            get => Options.InnerGradientTolerance;
            set => SetDoubleOption(value, Options.InnerGradientTolerance, v => Options.InnerGradientTolerance = v);
        }

        /// <summary>
        /// Optional explicit number of outer convexification cycles for the
        /// background run. Zero falls back to the generic page iteration count.
        /// </summary>
        public int ConvexificationOuterIterations
        {
            get => Options.OuterIterations;
            set => SetIntOption(Math.Max(0, value), Options.OuterIterations, v => Options.OuterIterations = v);
        }

        /// <summary>
        /// Early-stop tolerance for repeated outer cycles.
        /// </summary>
        public double ConvexificationOuterTolerance
        {
            get => Options.OuterTolerance;
            set => SetDoubleOption(value, Options.OuterTolerance, v => Options.OuterTolerance = v);
        }

        /// <summary>
        /// Line-search decay used when the inner descent update needs damping.
        /// </summary>
        public double ConvexificationLineSearchDecay
        {
            get => Options.LineSearchDecay;
            set => SetDoubleOption(value, Options.LineSearchDecay, v => Options.LineSearchDecay = v);
        }

        /// <summary>
        /// Smallest damping factor admitted by the convexification line search.
        /// </summary>
        public double ConvexificationMinimumInnerStep
        {
            get => Options.MinimumStepSize;
            set => SetDoubleOption(value, Options.MinimumStepSize, v => Options.MinimumStepSize = v);
        }

        /// <summary>
        /// Smallest admissible representative electrode size used in boundary scaling.
        /// </summary>
        public double ConvexificationElectrodeLengthFloor
        {
            get => Options.ElectrodeLengthFloor;
            set => SetDoubleOption(value, Options.ElectrodeLengthFloor, v => Options.ElectrodeLengthFloor = v);
        }

        /// <summary>
        /// Threshold above which large positivity shifts emit warnings.
        /// </summary>
        public double ConvexificationLargeShiftWarningThreshold
        {
            get => Options.LargeShiftWarningThreshold;
            set => SetDoubleOption(value, Options.LargeShiftWarningThreshold, v => Options.LargeShiftWarningThreshold = v);
        }

        /// <summary>
        /// Diagonal regularisation used during recovered-scale solves.
        /// </summary>
        public double ConvexificationSigmaRecoveryRegularization
        {
            get => Options.SigmaRecoveryRegularization;
            set => SetDoubleOption(value, Options.SigmaRecoveryRegularization, v => Options.SigmaRecoveryRegularization = v);
        }

        /// <summary>
        /// Positive floor enforced on the recovered scale field V.
        /// </summary>
        public double ConvexificationMinimumScale
        {
            get => Options.MinimumScale;
            set => SetDoubleOption(value, Options.MinimumScale, v => Options.MinimumScale = v);
        }

        /// <summary>
        /// H1-like smoothing weight applied to the recovered coefficient a(x).
        /// </summary>
        public double ConvexificationCoefficientSmoothingWeight
        {
            get => Options.CoefficientSmoothingWeight;
            set => SetDoubleOption(value, Options.CoefficientSmoothingWeight, v => Options.CoefficientSmoothingWeight = v);
        }

        /// <summary>
        /// Residual weight in the QRM-style V recovery stage.
        /// </summary>
        public double ConvexificationVRecoveryResidualWeight
        {
            get => Options.VRecoveryResidualWeight;
            set => SetDoubleOption(value, Options.VRecoveryResidualWeight, v => Options.VRecoveryResidualWeight = v);
        }

        /// <summary>
        /// Dirichlet collar weight in the QRM-style V recovery stage.
        /// </summary>
        public double ConvexificationVRecoveryDirichletWeight
        {
            get => Options.VRecoveryDirichletWeight;
            set => SetDoubleOption(value, Options.VRecoveryDirichletWeight, v => Options.VRecoveryDirichletWeight = v);
        }

        /// <summary>
        /// Neumann collar weight in the QRM-style V recovery stage.
        /// </summary>
        public double ConvexificationVRecoveryNeumannWeight
        {
            get => Options.VRecoveryNeumannWeight;
            set => SetDoubleOption(value, Options.VRecoveryNeumannWeight, v => Options.VRecoveryNeumannWeight = v);
        }

        /// <summary>
        /// H1-style smoothing weight in the QRM-style V recovery stage.
        /// </summary>
        public double ConvexificationVRecoveryGradientWeight
        {
            get => Options.VRecoveryGradientWeight;
            set => SetDoubleOption(value, Options.VRecoveryGradientWeight, v => Options.VRecoveryGradientWeight = v);
        }

        /// <summary>
        /// Mass anchor weight in the QRM-style V recovery stage.
        /// </summary>
        public double ConvexificationVRecoveryMassWeight
        {
            get => Options.VRecoveryMassWeight;
            set => SetDoubleOption(value, Options.VRecoveryMassWeight, v => Options.VRecoveryMassWeight = v);
        }

        /// <summary>
        /// X-component of the Carleman direction omega.
        /// </summary>
        public double ConvexificationOmegaX
        {
            get => GetOmegaComponent(0, 1.0);
            set => SetOmegaComponent(0, value);
        }

        /// <summary>
        /// Y-component of the Carleman direction omega.
        /// </summary>
        public double ConvexificationOmegaY
        {
            get => GetOmegaComponent(1, 0.0);
            set => SetOmegaComponent(1, value);
        }

        /// <summary>
        /// When true, drive derivatives wrap periodically around a full cycle.
        /// </summary>
        public bool ConvexificationUsePeriodicDriveDerivative
        {
            get => Options.UsePeriodicDriveDerivative;
            set => SetBoolOption(value, Options.UsePeriodicDriveDerivative, v => Options.UsePeriodicDriveDerivative = v);
        }

        /// <summary>
        /// Optional smoothing window applied before drive differentiation.
        /// </summary>
        public int ConvexificationDerivativeSmoothingWindow
        {
            get => Options.DerivativeSmoothingWindow;
            set => SetIntOption(Math.Max(0, value), Options.DerivativeSmoothingWindow, v => Options.DerivativeSmoothingWindow = v);
        }

        /// <summary>
        /// Number of smoothing passes applied before drive differentiation.
        /// </summary>
        public int ConvexificationDerivativeSmoothingPasses
        {
            get => Options.DerivativeSmoothingPasses;
            set => SetIntOption(Math.Max(0, value), Options.DerivativeSmoothingPasses, v => Options.DerivativeSmoothingPasses = v);
        }

        /// <summary>
        /// Enables periodic smoothing before differentiation when a full cycle is available.
        /// </summary>
        public bool ConvexificationUsePeriodicDerivativeSmoothing
        {
            get => Options.UsePeriodicDerivativeSmoothing;
            set => SetBoolOption(value, Options.UsePeriodicDerivativeSmoothing, v => Options.UsePeriodicDerivativeSmoothing = v);
        }

        /// <summary>
        /// When true, every electrode participates in the Neumann penalty.
        /// </summary>
        public bool ConvexificationUseAllElectrodesForNeumannPenalty
        {
            get => Options.UseAllElectrodesForNeumannPenalty;
            set => SetBoolOption(value, Options.UseAllElectrodesForNeumannPenalty, v => Options.UseAllElectrodesForNeumannPenalty = v);
        }

        /// <summary>
        /// Controls whether the recovered coefficient a(x) is averaged over the full cycle.
        /// </summary>
        public bool ConvexificationAverageRecoveredCoefficientAcrossCycle
        {
            get => Options.AverageRecoveredCoefficientAcrossCycle;
            set => SetBoolOption(value, Options.AverageRecoveredCoefficientAcrossCycle, v => Options.AverageRecoveredCoefficientAcrossCycle = v);
        }

        /// <summary>
        /// Short explanatory note shown in the dedicated convexification page.
        /// </summary>
        public string ConvexificationParameterNote =>
            "Uses the current FEM mesh from MeshingPage together with simulated ideal electrode voltages. The panel exposes only convexification-specific controls.";

        private void OnConvexificationPageViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ReconstructionParameters))
                return;

            ApplyConvexificationDefaults();
            NotifyConvexificationOptionBindingsChanged();
        }

        private void ApplyConvexificationDefaults()
        {
            Workspace.SetUseBlockConfiguration(false);
            Workspace.SetMeasurementSource(MeasurementSourceOption.Simulated);
            UseBlockConfiguration = false;
            ReconstructionParameters.DifferentialEquationSolver = DifferentialEquationSolver.FEM;
            ReconstructionParameters.UsePotentialDifferences = false;
            ReconstructionParameters.MeasurementNoiseType = MeasurementNoiseType.None;
            ReconstructionParameters.MeasurementNoiseAmplitude = 0.0;
        }

        private static bool AreClose(double left, double right)
            => Math.Abs(left - right) <= 1e-12;

        private void SetDoubleOption(double value,
                                     double currentValue,
                                     Action<double> setter,
                                     [CallerMemberName] string? propertyName = null)
        {
            if (!double.IsFinite(value) || AreClose(currentValue, value))
                return;

            setter(value);
            OnPropertyChanged(propertyName);
        }

        private void SetIntOption(int value,
                                  int currentValue,
                                  Action<int> setter,
                                  [CallerMemberName] string? propertyName = null)
        {
            if (currentValue == value)
                return;

            setter(value);
            OnPropertyChanged(propertyName);
        }

        private void SetBoolOption(bool value,
                                   bool currentValue,
                                   Action<bool> setter,
                                   [CallerMemberName] string? propertyName = null)
        {
            if (currentValue == value)
                return;

            setter(value);
            OnPropertyChanged(propertyName);
        }

        private double GetOmegaComponent(int index, double fallback)
        {
            if (Options.Omega == null || Options.Omega.Length <= index || !double.IsFinite(Options.Omega[index]))
                return fallback;

            return Options.Omega[index];
        }

        private void SetOmegaComponent(int index,
                                       double value,
                                       [CallerMemberName] string? propertyName = null)
        {
            if (!double.IsFinite(value))
                return;

            EnsureOmegaSize();
            if (AreClose(Options.Omega[index], value))
                return;

            Options.Omega[index] = value;
            OnPropertyChanged(propertyName);
        }

        private void EnsureOmegaSize()
        {
            if (Options.Omega != null && Options.Omega.Length >= 2)
                return;

            Options.Omega = new[] { 1.0, 0.0 };
        }

        private void NotifyConvexificationOptionBindingsChanged()
        {
            OnPropertyChanged(nameof(ConvexificationLambda));
            OnPropertyChanged(nameof(ConvexificationInteriorResidualWeight));
            OnPropertyChanged(nameof(ConvexificationBeta));
            OnPropertyChanged(nameof(ConvexificationEpsilon));
            OnPropertyChanged(nameof(ConvexificationD0));
            OnPropertyChanged(nameof(ConvexificationPositivityMargin));
            OnPropertyChanged(nameof(ConvexificationBoundaryDirichletWeight));
            OnPropertyChanged(nameof(ConvexificationBoundaryNeumannWeight));
            OnPropertyChanged(nameof(ConvexificationStepSize));
            OnPropertyChanged(nameof(ConvexificationInnerIterations));
            OnPropertyChanged(nameof(ConvexificationTolerance));
            OnPropertyChanged(nameof(ConvexificationInnerGradientTolerance));
            OnPropertyChanged(nameof(ConvexificationOuterIterations));
            OnPropertyChanged(nameof(ConvexificationOuterTolerance));
            OnPropertyChanged(nameof(ConvexificationLineSearchDecay));
            OnPropertyChanged(nameof(ConvexificationMinimumInnerStep));
            OnPropertyChanged(nameof(ConvexificationElectrodeLengthFloor));
            OnPropertyChanged(nameof(ConvexificationLargeShiftWarningThreshold));
            OnPropertyChanged(nameof(ConvexificationSigmaRecoveryRegularization));
            OnPropertyChanged(nameof(ConvexificationMinimumScale));
            OnPropertyChanged(nameof(ConvexificationCoefficientSmoothingWeight));
            OnPropertyChanged(nameof(ConvexificationVRecoveryResidualWeight));
            OnPropertyChanged(nameof(ConvexificationVRecoveryDirichletWeight));
            OnPropertyChanged(nameof(ConvexificationVRecoveryNeumannWeight));
            OnPropertyChanged(nameof(ConvexificationVRecoveryGradientWeight));
            OnPropertyChanged(nameof(ConvexificationVRecoveryMassWeight));
            OnPropertyChanged(nameof(ConvexificationOmegaX));
            OnPropertyChanged(nameof(ConvexificationOmegaY));
            OnPropertyChanged(nameof(ConvexificationUsePeriodicDriveDerivative));
            OnPropertyChanged(nameof(ConvexificationDerivativeSmoothingWindow));
            OnPropertyChanged(nameof(ConvexificationDerivativeSmoothingPasses));
            OnPropertyChanged(nameof(ConvexificationUsePeriodicDerivativeSmoothing));
            OnPropertyChanged(nameof(ConvexificationUseAllElectrodesForNeumannPenalty));
            OnPropertyChanged(nameof(ConvexificationAverageRecoveredCoefficientAcrossCycle));
            OnPropertyChanged(nameof(ConvexificationParameterNote));
        }
    }
}
