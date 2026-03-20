using Utility.Classes.Application;
using Utility.Tests;
using Utility.Tests.Validation;
using Utility.Composition;
using ServiceLayer;

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
            ServiceLayer.Settings.ApplyContainerRegistration();

            // Run built-in self-tests
            StartupSelfTests.RunAll();

            // Execute analytic validation suite comparing numerical solvers to
            // reference equations (Fourier modes, dipole, layered media, etc.)
            ValidationSelfTests.RunAll();

            // Workspace initialization
            Workspace.Initialize(new DefaultUser(1, "Test1", "Test1@factroymail.com"), null, null);
            ConvexificationReconstructionSelfTests.RunAll();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }

//        protected override Window CreateWindow(IActivationState activationState)
//        {
//            var mainPage = Utility.Composition.Container.ResolveObject<MainPage>();
//            var window = new Window(mainPage)
//            {
//                Title = "EITApplication"
//            };

//#if WINDOWS
//            var spotifyWindowSvc = Utility.Composition.Container.ResolveObject<SpotifyMiniPlayerWindowService>();

//            var openSpotifyBtn = new ImageButton
//            {
//                Source = "spotify_icon.png",
//                BackgroundColor = Colors.Transparent,
//                HeightRequest = 28,
//                WidthRequest = 28,
//                Padding = 6,
//                Command = new Command(() => spotifyWindowSvc.ShowOrActivate())
//            };

//            window.TitleBar = new TitleBar
//            {
//                Title = "EITApplication",
//                HeightRequest = 32,
//                TrailingContent = openSpotifyBtn
//            };

//            // Keeps the button clickable while preserving title-bar dragging elsewhere
//            window.TitleBar.PassthroughElements.Add(openSpotifyBtn);
//#endif

//            return window;
//        }
    }
}
