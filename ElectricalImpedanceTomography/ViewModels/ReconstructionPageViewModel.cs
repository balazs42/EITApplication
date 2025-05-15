using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseViewModel
    {
        private readonly IReconstructionService _reconstructionService;

        public IEnumerable<DifferentialEquationSolver> DifferentialEquationSolverOptions
           => Enum.GetValues(typeof(DifferentialEquationSolver))
                  .Cast<DifferentialEquationSolver>();

        public IEnumerable<RegularizationTechnique> RegularizationTechniqueOptions
            => Enum.GetValues(typeof(RegularizationTechnique))
                   .Cast<RegularizationTechnique>();

        public IEnumerable<ErrorMetric> ErrorMetricOptions
            => Enum.GetValues(typeof(ErrorMetric))
                   .Cast<ErrorMetric>();

        public IEnumerable<NumericSolver> NumericSolverOptions
            => Enum.GetValues(typeof(NumericSolver))
                   .Cast<NumericSolver>();

        public IEnumerable<NumericOptimizer> NumericOptimizerOptions
            => Enum.GetValues(typeof(NumericOptimizer))
                   .Cast<NumericOptimizer>();


        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters;

        public ReconstructionPageViewModel(IReconstructionService reconstructionService)
        {
            _reconstructionService = reconstructionService;

            ReconstructionParameters = new EITReconstructionParameters();
        }


        public async void OnStartReconstructionClicked(object sender, EventArgs e)
        {
            _reconstructionService.StartReconstruction(ReconstructionParameters);

            var result = await _reconstructionService.GetReconstructionResult();
        }

        public void OnReconstructionParametersChanged(object sender, EventArgs e)
        {
            if(sender is Picker picker)
            {                
                if(picker.BindingContext is EITReconstructionParameters reconstructionParameter)
                {
                    DifferentialEquationSolver differentialEquationSolver = reconstructionParameter.DifferentialEquationSolver;
                    RegularizationTechnique regularizationTechnique = reconstructionParameter.RegularizationTechnique;
                    ErrorMetric errorMetric = reconstructionParameter.ErrorMetric;
                    NumericSolver numericSolver = reconstructionParameter.NumericSolver;
                    NumericOptimizer numericOptimizer = reconstructionParameter.NumericOptimizer;

                    ReconstructionParameters = new EITReconstructionParameters(differentialEquationSolver,
                                                                               regularizationTechnique, 
                                                                               errorMetric, 
                                                                               numericSolver, 
                                                                               numericOptimizer);
                }
            }
        }

        private async void SetupLBMReconstruction()
        {
            var mesh = new LBMMesh(nx: 64, ny: 64);          // or read from file
            var prms = new EITReconstructionParameters
            {
                DifferentialEquationSolver = DifferentialEquationSolver.LatticeBoltzmannMethod,
                ErrorMetric = ErrorMetric.L2,
                RegularizationTechnique = RegularizationTechnique.None,
                NumericSolver = NumericSolver.GMRES,
                NumericOptimizer = NumericOptimizer.GradientBased
            };

            _reconstructionService.StartReconstruction(prms);
            var result = await _reconstructionService.GetReconstructionResult();
        }
    }
}
