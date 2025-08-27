using Utility.Classes.Application;
using Utility.Tests;
using Utility.Tests.Validation;
using Utility.Composition;

namespace ElectricalImpedanceTomography
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // Initialize Unity container, which will resolve DI objects
            Container.InitializeContainer();

            // Apply registrations will resolve the necessary objects
            Settings.ApplyContainerRegistration();
            ServiceLayer.Settings.ApplyContainerRegistration();

            // Run built-in self-tests
            StartupSelfTests.RunAll();

            // Execute analytic validation suite comparing numerical solvers to
            // reference equations (Fourier modes, dipole, layered media, etc.)
            ValidationSelfTests.RunAll();

            // Workspace initialization
            Workspace.Initialize(new User() { Id = 1, Name = "Test1", Email = "Test1@factroymail.com" }, null, null);
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}