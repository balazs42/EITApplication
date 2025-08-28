namespace ElectricalImpedanceTomography
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();      
            
            Routing.RegisterRoute(nameof(Views.MainPage), typeof(Views.MainPage));
            Routing.RegisterRoute(nameof(Views.DAQPage), typeof(Views.DAQPage));    
            Routing.RegisterRoute(nameof(Views.LBMReconstructionPage), typeof(Views.LBMReconstructionPage));
            Routing.RegisterRoute(nameof(Views.FEMReconstructionPage), typeof(Views.FEMReconstructionPage));
            Routing.RegisterRoute(nameof(Views.MeshingPage), typeof(Views.MeshingPage));
            Routing.RegisterRoute(nameof(Views.ReconstructionPage), typeof(Views.ReconstructionPage));
        }
    }
}
