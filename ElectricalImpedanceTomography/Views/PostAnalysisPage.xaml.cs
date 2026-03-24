using ElectricalImpedanceTomography.ViewModels;
using OxyPlot;
using OxyPlot.Maui.Skia;
using SkiaSharp;

namespace ElectricalImpedanceTomography.Views;

public partial class PostAnalysisPage : ContentPage
{
    private readonly PostAnalysisPageViewModel _viewModel;

    public PostAnalysisPage()
    {
        InitializeComponent();
        _viewModel = new PostAnalysisPageViewModel();
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.PlotExportRequested -= OnPlotExportRequested;
        _viewModel.PlotExportRequested += OnPlotExportRequested;
    }

    protected override void OnDisappearing()
    {
        _viewModel.PlotExportRequested -= OnPlotExportRequested;
        base.OnDisappearing();
    }

    private async void OnPlotExportRequested(object? sender, PlotExportRequest request)
    {
        if (request.Plot.PlotModel == null || request.Plot.PlotModel.Series.Count == 0)
        {
            await DisplayAlert("No plot data", "Select at least one dataset line before exporting a PNG.", "OK");
            return;
        }

        string suggestedName = BuildSafeFileName(string.IsNullOrWhiteSpace(request.Plot.Title)
            ? $"plot_{request.Plot.SelectedMetric}"
            : request.Plot.Title);

        string? fileName = await DisplayPromptAsync(
            "Save Plot PNG",
            "File name",
            initialValue: suggestedName,
            maxLength: 80,
            keyboard: Keyboard.Text);

        if (string.IsNullOrWhiteSpace(fileName))
            return;

        try
        {
            string exportDirectory = Path.Combine(FileSystem.AppDataDirectory, "post-analysis-plots");
            Directory.CreateDirectory(exportDirectory);

            string safeName = BuildSafeFileName(fileName);
            string outputPath = Path.Combine(exportDirectory, $"{safeName}.png");

            ExportPlotToPng(request.Plot.PlotModel, outputPath, 1600, 900);
            _viewModel.StatusMessage = $"Saved plot PNG to {outputPath}.";

            await DisplayAlert("Plot saved", $"PNG exported to:\n{outputPath}", "OK");
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"Failed to save plot PNG: {ex.Message}";
            await DisplayAlert("Export failed", ex.Message, "OK");
        }
    }

    private static void ExportPlotToPng(PlotModel model, string path, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var renderAssembly = typeof(PlotView).Assembly;
        var renderContextType = renderAssembly.GetType("OxyPlot.Maui.Skia.SkiaRenderContext")
            ?? throw new InvalidOperationException("Skia render context type could not be resolved.");
        var renderTargetType = renderAssembly.GetType("OxyPlot.Maui.Skia.RenderTarget")
            ?? throw new InvalidOperationException("Skia render target type could not be resolved.");

        var renderContextInstance = Activator.CreateInstance(renderContextType)
            ?? throw new InvalidOperationException("Skia render context could not be created.");
        using var disposableRenderContext = renderContextInstance as IDisposable;

        renderContextType.GetProperty("SkCanvas")?.SetValue(renderContextInstance, canvas);
        renderContextType.GetProperty("DpiScale")?.SetValue(renderContextInstance, 1.0);
        renderContextType.GetProperty("RenderTarget")?.SetValue(
            renderContextInstance,
            Enum.Parse(renderTargetType, "PixelGraphic"));

        if (renderContextInstance is not IRenderContext renderContext)
            throw new InvalidOperationException("Skia render context does not implement IRenderContext.");

        ((IPlotModel)model).Update(true);
        ((IPlotModel)model).Render(renderContext, new OxyRect(0, 0, width, height));

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
    }

    private static string BuildSafeFileName(string name)
    {
        string cleaned = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? "plot_export" : cleaned;
    }
}
