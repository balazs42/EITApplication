using Utility.Tests;
using Utility.Tests.Validation;

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

            // Run built-in self-tests
            StartupSelfTests.RunAll();

            // Execute analytic validation suite comparing numerical solvers to
            // reference equations (Fourier modes, dipole, layered media, etc.)
            ValidationSelfTests.RunAll();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}