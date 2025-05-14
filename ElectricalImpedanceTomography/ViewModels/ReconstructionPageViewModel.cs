using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class ReconstructionPageViewModel : BaseViewModel
    {
        private readonly IReconstructionService _reconstructionService;

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
            if(sender is Button button)
            {                
                if(button.BindingContext is EITReconstructionParameters reconstructionParameter)
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
    }
}
