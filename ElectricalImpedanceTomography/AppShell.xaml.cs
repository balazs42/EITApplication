using ElectricalImpedanceTomography.Controls;
﻿namespace ElectricalImpedanceTomography
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
            Routing.RegisterRoute(nameof(Views.ReconstructionConfigurationPage), typeof(Views.ReconstructionConfigurationPage));

            Navigated += OnNavigated;

            if (CurrentPage is ContentPage page)
                page.FindByName<NavBarControl>("NavBar")?.RefreshCurrentPage();
        }

        private void OnNavigated(object? sender, ShellNavigatedEventArgs e)
        {
            if (CurrentPage is ContentPage page)
            {
                var navBar = page.FindByName<NavBarControl>("NavBar");
                navBar?.RefreshCurrentPage();
            }
        }
    }
}
