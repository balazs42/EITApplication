using ServiceLayer;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class DAQPageViewModel : BaseViewModel
    {
        private readonly IDAQService _daqService;

        public DAQPageViewModel(IDAQService daqService)
        {
            _daqService = daqService;
        }
    }
}
