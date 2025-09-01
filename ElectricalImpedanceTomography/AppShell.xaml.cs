namespace ElectricalImpedanceTomography
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();      
            
            // Registering routes for the shell routing. It is a must to register pages, so navigation knows where to go.
            Routing.RegisterRoute(nameof(Views.MainPage), typeof(Views.MainPage));
            Routing.RegisterRoute(nameof(Views.DAQPage), typeof(Views.DAQPage));    
            Routing.RegisterRoute(nameof(Views.MeshingPage), typeof(Views.MeshingPage));
            Routing.RegisterRoute(nameof(Views.ReconstructionPage), typeof(Views.ReconstructionPage));
        }
    }
}
