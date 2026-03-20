using ElectricalImpedanceTomography.ViewModels;
using System;
using System.Diagnostics;
using System.Linq;
using Utility.Classes.Application;
using Utility.Composition;
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
            Container.InitializeContainer();

            // Apply registrations will resolve the necessary objects
            ServiceLayer.Settings.ApplyContainerRegistration();

            // Run built-in self-tests
            StartupSelfTests.RunAll();

            // Execute analytic validation suite comparing numerical solvers to
            // reference equations (Fourier modes, dipole, layered media, etc.)
            ValidationSelfTests.RunAll();

            // Workspace initialization
            Workspace.ResetApplicationLifetimeState();
            Workspace.Initialize(new DefaultUser(1, "Test1", "Test1@factroymail.com"), null, null);
            //ConvexificationReconstructionSelfTests.RunAll();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            Workspace.ResetApplicationLifetimeState();
            var window = new Window(new AppShell());
            window.Destroying += OnWindowDestroying;
            return window;
        }

        private static void OnWindowDestroying(object? sender, EventArgs e)
        {
            Workspace.BeginApplicationShutdown();

            try
            {
                if (sender is not Window window)
                    return;

                var activePage = ResolveActivePage(window.Page);
                if (activePage?.BindingContext is ReconstructionPageViewModel viewModel)
                    viewModel.ShutdownForApplicationExit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Application shutdown cleanup failed: {ex}");
            }
        }

        private static Page? ResolveActivePage(Page? root)
        {
            if (root is null)
                return null;

            if (root is FlyoutPage flyoutPage)
                return ResolveActivePage(flyoutPage.Detail);

            if (root is TabbedPage tabbedPage)
                return ResolveActivePage(tabbedPage.CurrentPage);

            if (root is NavigationPage navigationPage)
                return ResolveActivePage(navigationPage.CurrentPage);

            if (root is Shell shell)
            {
                var currentPageProperty = typeof(Shell).GetProperty("CurrentPage");
                if (currentPageProperty?.GetValue(shell) is Page shellCurrentPage)
                    return ResolveActivePage(shellCurrentPage);

                if (shell.Navigation?.NavigationStack?.LastOrDefault() is Page navigationStackPage)
                    return ResolveActivePage(navigationStackPage);
            }

            return root;
        }

//        protected override Window CreateWindow(IActivationState activationState)
//        {
//            var mainPage = Utility.Composition.Container.ResolveObject<MainPage>();
//            var window = new Window(mainPage)
//            {
//                Title = "EITApplication"
//            };
//
//#if WINDOWS
//            var spotifyWindowSvc = Utility.Composition.Container.ResolveObject<SpotifyMiniPlayerWindowService>();
//
//            var openSpotifyBtn = new ImageButton
//            {
//                Source = "spotify_icon.png",
//                BackgroundColor = Colors.Transparent,
//                HeightRequest = 28,
//                WidthRequest = 28,
//                Padding = 6,
//                Command = new Command(() => spotifyWindowSvc.ShowOrActivate())
//            };
//
//            window.TitleBar = new TitleBar
//            {
//                Title = "EITApplication",
//                HeightRequest = 32,
//                TrailingContent = openSpotifyBtn
//            };
//
//            // Keeps the button clickable while preserving title-bar dragging elsewhere
//            window.TitleBar.PassthroughElements.Add(openSpotifyBtn);
//#endif
//
//            return window;
//        }
    }
}
