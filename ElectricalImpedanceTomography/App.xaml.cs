using Utility.Tests;

namespace ElectricalImpedanceTomography
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // Initialize Unity container, which will resolve DI objects
            Utility.Composition.Container.InitializeContainer();

            // Apply registrations will resolve the necessary objects
            Utility.Composition.Settings.ApplyContainerRegistration();
            ServiceLayer.Settings.ApplyContainerRegistration();

            // TODO: run tests
            StartupSelfTests.RunAll();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}