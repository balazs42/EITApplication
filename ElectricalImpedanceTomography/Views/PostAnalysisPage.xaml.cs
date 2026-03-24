using ElectricalImpedanceTomography.ViewModels;
using OxyPlot;
using OxyPlot.Maui.Skia;
using SkiaSharp;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using MauiRoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

namespace ElectricalImpedanceTomography.Views;

public partial class PostAnalysisPage : ContentPage
{
    private readonly PostAnalysisPageViewModel _viewModel;
    private bool _resizePromptOpen;

    public PostAnalysisPage()
    {
        InitializeComponent();
        _viewModel = new PostAnalysisPageViewModel();
        BindingContext = _viewModel;
        _viewModel.Plots.CollectionChanged += OnPlotsCollectionChanged;
        AttachPlotPropertyHandlers(_viewModel.Plots);
        BuildPlotCanvas();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.PlotExportRequested -= OnPlotExportRequested;
        _viewModel.PlotExportRequested += OnPlotExportRequested;
        _viewModel.PlotResizeRequested -= OnPlotResizeRequested;
        _viewModel.PlotResizeRequested += OnPlotResizeRequested;
        BuildPlotCanvas();
    }

    protected override void OnDisappearing()
    {
        _viewModel.PlotExportRequested -= OnPlotExportRequested;
        _viewModel.PlotResizeRequested -= OnPlotResizeRequested;
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
        SetPropertyValue(renderContextType.GetProperty("DpiScale"), renderContextInstance, 1f);
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

    private async void OnPlotResizeRequested(object? sender, PlotResizeRequest request)
    {
        await ShowResizeOptionsAsync(request.Plot);
    }

    private async void OnResizeHandlePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType != GestureStatus.Completed || _resizePromptOpen)
            return;

        if (sender is not BindableObject bindableObject || bindableObject.BindingContext is not AnalysisPlotViewModel plot)
            return;

        await ShowResizeOptionsAsync(plot);
    }

    private async Task ShowResizeOptionsAsync(AnalysisPlotViewModel plot)
    {
        _resizePromptOpen = true;
        try
        {
            string? option = await DisplayActionSheet(
                $"Snap {plot.Title}",
                "Cancel",
                null,
                "Top-left cell",
                "Top-right cell",
                "Bottom-left cell",
                "Bottom-right cell",
                "Top row",
                "Bottom row",
                "Left column",
                "Right column",
                "Full grid");

            var snapOption = option switch
            {
                "Top-left cell" => PlotCanvasSnapOption.TopLeft,
                "Top-right cell" => PlotCanvasSnapOption.TopRight,
                "Bottom-left cell" => PlotCanvasSnapOption.BottomLeft,
                "Bottom-right cell" => PlotCanvasSnapOption.BottomRight,
                "Top row" => PlotCanvasSnapOption.TopRow,
                "Bottom row" => PlotCanvasSnapOption.BottomRow,
                "Left column" => PlotCanvasSnapOption.LeftColumn,
                "Right column" => PlotCanvasSnapOption.RightColumn,
                "Full grid" => PlotCanvasSnapOption.FullGrid,
                _ => (PlotCanvasSnapOption?)null
            };

            if (snapOption.HasValue)
            {
                _viewModel.ApplyCanvasSnap(plot, snapOption.Value);
                BuildPlotCanvas();
            }
        }
        finally
        {
            _resizePromptOpen = false;
        }
    }

    private void OnPlotsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var plot in e.OldItems.OfType<AnalysisPlotViewModel>())
                plot.PropertyChanged -= OnPlotPropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (var plot in e.NewItems.OfType<AnalysisPlotViewModel>())
                plot.PropertyChanged += OnPlotPropertyChanged;
        }

        MainThread.BeginInvokeOnMainThread(BuildPlotCanvas);
    }

    private void AttachPlotPropertyHandlers(IEnumerable<AnalysisPlotViewModel> plots)
    {
        foreach (var plot in plots)
            plot.PropertyChanged += OnPlotPropertyChanged;
    }

    private void OnPlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(BuildPlotCanvas);
    }

    private void BuildPlotCanvas()
    {
        PlotCanvasGrid.Children.Clear();

        var visiblePlots = _viewModel.Plots.Where(plot => plot.IsVisibleOnCanvas).ToList();
        if (visiblePlots.Count == 0)
        {
            PlotCanvasGrid.Children.Add(new Label
            {
                Text = "Open a plot from the bottom bar, then drag any corner handle or press Snap to choose a 1-cell, 2-cell, or full-grid layout.",
                TextColor = Color.FromArgb("#AAAAAA"),
                FontSize = 12,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center
            });
            return;
        }

        foreach (var plot in visiblePlots)
        {
            var card = CreatePlotCard(plot);
            Grid.SetRow(card, plot.CanvasRow);
            Grid.SetColumn(card, plot.CanvasColumn);
            Grid.SetRowSpan(card, plot.CanvasRowSpan);
            Grid.SetColumnSpan(card, plot.CanvasColumnSpan);
            PlotCanvasGrid.Children.Add(card);
        }
    }

    private View CreatePlotCard(AnalysisPlotViewModel plot)
    {
        var border = new Border
        {
            BindingContext = plot,
            BackgroundColor = Color.FromArgb("#252535"),
            Stroke = plot.IsSelected ? Color.FromArgb("#4CC9F0") : Color.FromArgb("#444444"),
            StrokeThickness = plot.IsSelected ? 2 : 1,
            StrokeShape = new MauiRoundRectangle { CornerRadius = new CornerRadius(10) },
            Padding = 12
        };
        border.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = _viewModel.SelectPlotCommand,
            CommandParameter = plot
        });

        var rootGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = GridLength.Auto }
            },
            RowSpacing = 10
        };

        var headerGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };

        var titleStack = new VerticalStackLayout { Spacing = 2 };
        var titleLabel = new Label
        {
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#E0E0E0"),
            LineBreakMode = LineBreakMode.TailTruncation
        };
        titleLabel.SetBinding(Label.TextProperty, nameof(AnalysisPlotViewModel.Title));

        var badgeLabel = new Label
        {
            FontSize = 11,
            TextColor = Color.FromArgb("#4CC9F0")
        };
        badgeLabel.SetBinding(Label.TextProperty, nameof(AnalysisPlotViewModel.PlotBadge));
        titleStack.Add(titleLabel);
        titleStack.Add(badgeLabel);
        headerGrid.Add(titleStack);

        headerGrid.Add(CreateHeaderButton("Snap", _viewModel.RequestPlotResizeCommand, plot, null), 1);
        headerGrid.Add(CreateHeaderButton("PNG", _viewModel.RequestPlotExportCommand, plot, Color.FromArgb("#2E8B57")), 2);
        headerGrid.Add(CreateHeaderButton("_", _viewModel.MinimizePlotCommand, plot, null, 44), 3);

        var plotHost = new Grid();
        var plotView = new PlotView
        {
            BackgroundColor = Colors.Transparent,
            MinimumHeightRequest = 180,
            VerticalOptions = LayoutOptions.FillAndExpand,
            HorizontalOptions = LayoutOptions.FillAndExpand
        };
        plotView.SetBinding(PlotView.ModelProperty, nameof(AnalysisPlotViewModel.PlotModel));
        plotHost.BindingContext = plot;
        plotHost.Children.Add(plotView);
        plotHost.Children.Add(CreateResizeHandle(plot, LayoutOptions.Start, LayoutOptions.Start));
        plotHost.Children.Add(CreateResizeHandle(plot, LayoutOptions.End, LayoutOptions.Start));
        plotHost.Children.Add(CreateResizeHandle(plot, LayoutOptions.Start, LayoutOptions.End));
        plotHost.Children.Add(CreateResizeHandle(plot, LayoutOptions.End, LayoutOptions.End));

        var footerGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 10
        };
        var lineSummary = new Label
        {
            FontSize = 11,
            TextColor = Color.FromArgb("#E0E0E0")
        };
        lineSummary.SetBinding(Label.TextProperty, nameof(AnalysisPlotViewModel.LineSummary));
        var statusLabel = new Label
        {
            FontSize = 10,
            TextColor = Color.FromArgb("#AAAAAA"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 2
        };
        statusLabel.SetBinding(Label.TextProperty, nameof(AnalysisPlotViewModel.PlotStatus));
        footerGrid.Add(lineSummary);
        footerGrid.Add(statusLabel, 1);

        rootGrid.Add(headerGrid);
        rootGrid.Add(plotHost, 0, 1);
        rootGrid.Add(footerGrid, 0, 2);
        border.Content = rootGrid;
        return border;
    }

    private Button CreateHeaderButton(string text, ICommand command, object commandParameter, Color? backgroundColor, double width = 72)
    {
        return new Button
        {
            Text = text,
            Command = command,
            CommandParameter = commandParameter,
            BackgroundColor = backgroundColor ?? Color.FromArgb("#3A3A4E"),
            TextColor = Color.FromArgb("#E0E0E0"),
            BorderColor = Color.FromArgb("#444444"),
            BorderWidth = 1,
            CornerRadius = 6,
            HeightRequest = 34,
            WidthRequest = width,
            FontSize = 13
        };
    }

    private Border CreateResizeHandle(AnalysisPlotViewModel plot, LayoutOptions horizontal, LayoutOptions vertical)
    {
        var recognizer = new PanGestureRecognizer
        {
            BindingContext = plot
        };
        recognizer.PanUpdated += OnResizeHandlePanUpdated;

        var handle = new Border
        {
            WidthRequest = 14,
            HeightRequest = 14,
            BackgroundColor = Color.FromArgb("#4CC9F0"),
            StrokeThickness = 0,
            Opacity = 0.85,
            HorizontalOptions = horizontal,
            VerticalOptions = vertical
        };
        handle.GestureRecognizers.Add(recognizer);
        return handle;
    }

    private static void SetPropertyValue(System.Reflection.PropertyInfo? propertyInfo, object target, object value)
    {
        if (propertyInfo == null)
            return;

        if (propertyInfo.PropertyType == typeof(float))
        {
            propertyInfo.SetValue(target, Convert.ToSingle(value));
            return;
        }

        if (propertyInfo.PropertyType == typeof(double))
        {
            propertyInfo.SetValue(target, Convert.ToDouble(value));
            return;
        }

        propertyInfo.SetValue(target, value);
    }

    private static string BuildSafeFileName(string name)
    {
        string cleaned = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? "plot_export" : cleaned;
    }
}
