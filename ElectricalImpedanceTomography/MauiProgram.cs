using CommunityToolkit.Maui;
using ElectricalImpedanceTomography.Views;
using Microsoft.Extensions.Logging;
using OxyPlot.Maui.Skia;
using SkiaSharp.Views.Maui.Controls.Hosting;
using Utility.Classes.Spotify;

namespace ElectricalImpedanceTomography
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit()      // Register the toolkit
                .UseSkiaSharp()                 // Skia sharp for the canvases
                .UseOxyPlotSkia()               // Oxyplot for the plots
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    fonts.AddFont("SFPRODISPLAYREGULAR.otf", "SF Pro Text");
                });

            builder.Services.AddSingleton<ISpotifyTokenStore, SpotifyTokenStore>();
            builder.Services.AddSingleton<SpotifyPkceLoopbackAuth>();
            builder.Services.AddSingleton<SpotifySession>();
            builder.Services.AddSingleton<SpotifyPlayerApi>();

            builder.Services.AddTransient<SpotifyMiniPlayerViewModel>();
            builder.Services.AddTransient<SpotifyMiniPlayerPage>();
            builder.Services.AddSingleton<SpotifyMiniPlayerWindowService>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
