using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;
using OxyPlot.Maui.Skia;
using CommunityToolkit.Maui;

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


#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
