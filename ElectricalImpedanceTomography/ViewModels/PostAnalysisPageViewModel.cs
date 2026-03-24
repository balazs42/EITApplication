using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

#if WINDOWS
using MauiApplication = Microsoft.Maui.Controls.Application;
using WinUiWindow = Microsoft.UI.Xaml.Window;
using Windows.Storage.Pickers;
using WinRT.Interop;
#endif

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class PostAnalysisPageViewModel : ObservableObject
    {
        public const int MaxPlots = 4;
        public const int MaxLinesPerPlot = 16;
        private const string MetricsFileName = "iteration_metrics.csv";
        private const string IterationColumnName = "Iteration";
        private const string DefaultXAxisLabel = "Iteration step";
        private const string DefaultMetric = "Residual";
        private static readonly string[] IterationColumnAliases =
        [
            "Iteration",
            "Iterations",
            "IterationCount",
            "Iteration Step",
            "Step"
        ];

        private static readonly string[] PreferredMetricOrder =
        [
            "Residual",
            "RMSE",
            "MAE",
            "MAPE",
            "PSNR",
            "SSIM",
            "Correlation",
            "RMSEImprovement",
            "MAEImprovement"
        ];

        private static readonly OxyColor[] DatasetPalette =
        [
            OxyColor.Parse("#4CC9F0"),
            OxyColor.Parse("#F8961E"),
            OxyColor.Parse("#43AA8B"),
            OxyColor.Parse("#F94144"),
            OxyColor.Parse("#90BE6D"),
            OxyColor.Parse("#577590"),
            OxyColor.Parse("#F9C74F"),
            OxyColor.Parse("#C77DFF"),
            OxyColor.Parse("#4895EF"),
            OxyColor.Parse("#F28482"),
            OxyColor.Parse("#74C69D"),
            OxyColor.Parse("#FFB703"),
            OxyColor.Parse("#B8C0FF"),
            OxyColor.Parse("#FB8500"),
            OxyColor.Parse("#A8DADC"),
            OxyColor.Parse("#E76F51")
        ];

        private int _nextColorIndex;

        public ObservableCollection<ImportedMetricDatasetViewModel> ImportedDatasets { get; } = [];
        public ObservableCollection<string> AvailableMetrics { get; } = [];
        public ObservableCollection<AnalysisPlotViewModel> Plots { get; } = [];

        [ObservableProperty]
        private ImportedMetricDatasetViewModel? selectedDataset;

        [ObservableProperty]
        private AnalysisPlotViewModel? selectedPlot;

        [ObservableProperty]
        private string statusMessage = "Import folders that contain iteration_metrics.csv to start comparing runs.";

        [ObservableProperty]
        private string importSummary = "No iteration metrics loaded";

        [ObservableProperty]
        private string plotSummary = "1 plot available";

        [ObservableProperty]
        private int plotGridSpan = 1;

        [ObservableProperty]
        private bool hasImportedDatasets;

        [ObservableProperty]
        private bool canAddPlot = true;

        [ObservableProperty]
        private bool canRemoveSelectedPlot;

        [ObservableProperty]
        private bool canRemoveSelectedDataset;

        public event EventHandler<PlotExportRequest>? PlotExportRequested;
        public event EventHandler<PlotResizeRequest>? PlotResizeRequested;
        public int PlotCount => Plots.Count;
        public AnalysisPlotViewModel? PlotSlot1 => GetPlotAt(0);
        public AnalysisPlotViewModel? PlotSlot2 => GetPlotAt(1);
        public AnalysisPlotViewModel? PlotSlot3 => GetPlotAt(2);
        public AnalysisPlotViewModel? PlotSlot4 => GetPlotAt(3);
        public bool HasPlotSlot1 => PlotSlot1 != null;
        public bool HasPlotSlot2 => PlotSlot2 != null;
        public bool HasPlotSlot3 => PlotSlot3 != null;
        public bool HasPlotSlot4 => PlotSlot4 != null;
        public bool UseSinglePlotLayout => PlotCount <= 1;
        public bool UseTwoPlotLayout => PlotCount == 2;
        public bool UseQuadPlotLayout => PlotCount >= 3;
        public bool HasVisiblePlots => Plots.Any(plot => plot.IsVisibleOnCanvas);

        public PostAnalysisPageViewModel()
        {
            ImportedDatasets.CollectionChanged += OnImportedDatasetsCollectionChanged;
            Plots.CollectionChanged += OnPlotsCollectionChanged;

            ResetAvailableMetrics();
            AddPlotCore(ResolveDefaultMetric(), autoSelectImportedDatasets: false);
            StatusMessage = "Add one or more folders, then select the datasets you want to draw on the active plot.";
        }

        [RelayCommand(FlowExceptionsToTaskScheduler = true)]
        private async Task AddFolderAsync()
        {
            await ImportFromPickerAsync("Select a reconstruction export folder", recursiveSearch: false);
        }

        [RelayCommand(FlowExceptionsToTaskScheduler = true)]
        private async Task ScanFolderTreeAsync()
        {
            await ImportFromPickerAsync("Select a folder tree to scan for iteration_metrics.csv", recursiveSearch: true);
        }

        [RelayCommand]
        private void RemoveSelectedDataset()
        {
            if (SelectedDataset == null)
                return;

            ImportedDatasets.Remove(SelectedDataset);
            StatusMessage = "Removed the selected dataset.";
        }

        [RelayCommand]
        private void ClearDatasets()
        {
            ImportedDatasets.Clear();
            SelectedDataset = null;
            ResetAvailableMetrics();

            foreach (var plot in Plots)
            {
                plot.SyncDatasets(Array.Empty<ImportedMetricDatasetViewModel>());
                plot.RefreshPlotModel();
            }

            StatusMessage = "Cleared all imported iteration metrics.";
        }

        [RelayCommand]
        private void AddPlot()
        {
            if (Plots.Count >= MaxPlots)
            {
                StatusMessage = $"At most {MaxPlots} plots can be shown at once.";
                return;
            }

            var plot = AddPlotCore(ResolveMetricForNewPlot(), autoSelectImportedDatasets: false);
            if (SelectedPlot != null)
                plot.CopyVisibleSelectionsFrom(SelectedPlot);

            SelectedPlot = plot;
            StatusMessage = $"Added plot {Plots.Count}.";
        }

        [RelayCommand]
        private void RemoveSelectedPlot()
        {
            if (SelectedPlot == null || Plots.Count <= 1)
            {
                StatusMessage = "At least one plot must remain on the page.";
                return;
            }

            var index = Plots.IndexOf(SelectedPlot);
            if (index < 0)
                index = Plots.Count - 1;

            Plots.Remove(SelectedPlot);
            SelectedPlot = Plots[Math.Max(0, Math.Min(index - 1, Plots.Count - 1))];
            ApplyDefaultCanvasLayout();
            StatusMessage = "Removed the selected plot.";
        }

        [RelayCommand]
        private void SelectPlot(AnalysisPlotViewModel? plot)
        {
            if (plot == null)
                return;

            SelectedPlot = plot;
            StatusMessage = $"Selected {plot.Title}.";
        }

        [RelayCommand]
        private void FillSelectedPlot()
        {
            if (SelectedPlot == null)
            {
                StatusMessage = "Select a plot first.";
                return;
            }

            if (ImportedDatasets.Count == 0)
            {
                StatusMessage = "Import one or more iteration metric folders before filling a plot.";
                return;
            }

            SelectedPlot.SelectFirstAvailableDatasets();
            SelectedPlot.RefreshPlotModel();
            StatusMessage = $"Filled {SelectedPlot.Title}. {SelectedPlot.PlotStatus}";
        }

        [RelayCommand]
        private void ClearSelectedPlotLines()
        {
            if (SelectedPlot == null)
            {
                StatusMessage = "Select a plot first.";
                return;
            }

            SelectedPlot.ClearVisibleSelections();
            StatusMessage = $"Cleared {SelectedPlot.Title}.";
        }

        [RelayCommand]
        private void RequestPlotExport(AnalysisPlotViewModel? plot)
        {
            var targetPlot = plot ?? SelectedPlot;
            if (targetPlot == null)
                return;

            PlotExportRequested?.Invoke(this, new PlotExportRequest(targetPlot));
        }

        [RelayCommand]
        private void MinimizePlot(AnalysisPlotViewModel? plot)
        {
            if (plot == null)
                return;

            plot.IsMinimized = true;

            if (ReferenceEquals(SelectedPlot, plot))
                SelectedPlot = Plots.FirstOrDefault(candidate => !candidate.IsMinimized) ?? Plots.FirstOrDefault();

            ApplyDefaultCanvasLayout();
            StatusMessage = $"{plot.Title} minimized.";
        }

        [RelayCommand]
        private void ActivatePlotWindow(AnalysisPlotViewModel? plot)
        {
            if (plot == null)
                return;

            bool wasMinimized = plot.IsMinimized;
            plot.IsMinimized = false;

            if (wasMinimized)
                ApplyDefaultCanvasLayout();

            SelectedPlot = plot;
            StatusMessage = wasMinimized ? $"{plot.Title} restored." : $"Selected {plot.Title}.";
        }

        [RelayCommand]
        private void RequestPlotResize(AnalysisPlotViewModel? plot)
        {
            if (plot == null)
                return;

            plot.IsMinimized = false;
            SelectedPlot = plot;
            PlotResizeRequested?.Invoke(this, new PlotResizeRequest(plot));
        }

        partial void OnSelectedDatasetChanged(ImportedMetricDatasetViewModel? value)
        {
            CanRemoveSelectedDataset = value != null;
        }

        partial void OnSelectedPlotChanged(AnalysisPlotViewModel? value)
        {
            foreach (var plot in Plots)
                plot.IsSelected = ReferenceEquals(plot, value);

            CanRemoveSelectedPlot = value != null && Plots.Count > 1;
        }

        private AnalysisPlotViewModel AddPlotCore(string metric, bool autoSelectImportedDatasets)
        {
            var plot = new AnalysisPlotViewModel(Plots.Count + 1, metric);
            plot.SyncDatasets(ImportedDatasets);
            plot.StatusChanged += OnPlotStatusChanged;

            if (autoSelectImportedDatasets)
                plot.SelectFirstAvailableDatasets();

            Plots.Add(plot);
            ApplyDefaultCanvasLayout();
            return plot;
        }

        private async Task ImportFromPickerAsync(string title, bool recursiveSearch)
        {
            try
            {
                string? folder = await PickFolderAsync(title);
                if (string.IsNullOrWhiteSpace(folder))
                    return;

                ImportFromFolders([folder], recursiveSearch);
            }
            catch (Exception ex)
            {
                HandleUnexpectedException("Failed to import iteration metrics", ex);
            }
        }

        private void ImportFromFolders(IEnumerable<string> folderPaths, bool recursiveSearch)
        {
            var csvPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skippedLocationCount = 0;

            foreach (var folderPath in folderPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                int skippedCount;
                foreach (var csvPath in FindMetricFiles(folderPath, recursiveSearch, out skippedCount))
                    csvPaths.Add(csvPath);

                skippedLocationCount += skippedCount;
            }

            ImportFromMetricFiles(csvPaths);

            if (skippedLocationCount > 0 && csvPaths.Count > 0)
                StatusMessage += $" Skipped {skippedLocationCount} inaccessible folder(s) while searching.";
        }

        private void ImportFromMetricFiles(IEnumerable<string> csvPaths)
        {
            var normalizedPaths = csvPaths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedPaths.Count == 0)
            {
                StatusMessage = "No iteration_metrics.csv files were found in the selected folder.";
                return;
            }

            int loadedCount = 0;
            int duplicateCount = 0;
            var failedMessages = new List<string>();
            var newDatasets = new List<ImportedMetricDatasetViewModel>();

            foreach (var csvPath in normalizedPaths)
            {
                if (ImportedDatasets.Any(dataset => string.Equals(dataset.MetricsFilePath,
                                                                  csvPath,
                                                                  StringComparison.OrdinalIgnoreCase)))
                {
                    duplicateCount++;
                    continue;
                }

                try
                {
                    var dataset = CreateDataset(csvPath);
                    ImportedDatasets.Add(dataset);
                    newDatasets.Add(dataset);
                    loadedCount++;
                }
                catch (Exception ex)
                {
                    failedMessages.Add($"{Path.GetDirectoryName(csvPath)}: {ex.Message}");
                }
            }

            if (loadedCount > 0)
            {
                try
                {
                    SelectedDataset ??= newDatasets.FirstOrDefault();

                    if (Plots.Count == 0)
                        AddPlotCore(ResolveDefaultMetric(), autoSelectImportedDatasets: false);

                    foreach (var plot in Plots)
                    {
                        plot.SyncDatasets(ImportedDatasets);
                        plot.RefreshPlotModel();
                    }

                    SelectedPlot ??= Plots[0];
                    ApplyDefaultCanvasLayout();
                }
                catch (Exception ex)
                {
                    failedMessages.Add($"Post-import plot refresh failed: {ex.Message}");
                    Debug.WriteLine($"Post-analysis plot refresh failed after import: {ex}");
                }
            }

            var summaryParts = new List<string>();
            if (loadedCount > 0)
                summaryParts.Add($"Loaded {loadedCount} dataset(s)");
            if (duplicateCount > 0)
                summaryParts.Add($"skipped {duplicateCount} duplicate(s)");
            if (failedMessages.Count > 0)
                summaryParts.Add($"failed {failedMessages.Count} import(s)");

            StatusMessage = summaryParts.Count > 0
                ? string.Join(", ", summaryParts) + "."
                : "No new iteration metrics were imported.";

            if (failedMessages.Count > 0)
                StatusMessage += $" First issue: {failedMessages[0]}";
        }

        private ImportedMetricDatasetViewModel CreateDataset(string csvPath)
        {
            var lines = File.ReadAllLines(csvPath);
            if (lines.Length == 0)
                throw new InvalidOperationException("The CSV file is empty.");

            char delimiter = DetectDelimiter(lines[0]);
            var rawHeaders = SplitDelimitedLine(lines[0], delimiter)
                .Select(CleanHeader)
                .ToList();
            if (rawHeaders.Count == 0)
                throw new InvalidOperationException("The CSV file has no readable headers.");

            int iterationColumnIndex = rawHeaders.FindIndex(IsIterationColumn);
            if (iterationColumnIndex < 0)
                throw new InvalidOperationException("The CSV file does not contain an Iteration column.");

            var headers = BuildUniqueHeaders(rawHeaders);

            var tableColumns = new ObservableCollection<MetricTableColumnViewModel>(
                headers.Select(header => new MetricTableColumnViewModel(header)));
            var tableRows = new ObservableCollection<MetricTableRowViewModel>();
            var iterationValues = new List<double>();
            var numericColumns = headers.ToDictionary(header => header,
                                                      _ => new List<double?>(),
                                                      StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                var cells = SplitDelimitedLine(rawLine, delimiter).ToList();
                while (cells.Count < headers.Count)
                    cells.Add(string.Empty);

                var rowCells = new ObservableCollection<MetricTableCellViewModel>();
                double iterationValue = tableRows.Count + 1;

                for (int index = 0; index < headers.Count; index++)
                {
                    string value = index < cells.Count ? cells[index] : string.Empty;
                    rowCells.Add(new MetricTableCellViewModel(value));

                    if (TryParseNullableDouble(value, out var numericValue))
                        numericColumns[headers[index]].Add(numericValue);
                    else
                        numericColumns[headers[index]].Add(null);

                    if (index == iterationColumnIndex && numericValue.HasValue)
                        iterationValue = numericValue.Value;
                }

                iterationValues.Add(iterationValue);
                tableRows.Add(new MetricTableRowViewModel(rowCells));
            }

            if (tableRows.Count == 0)
                throw new InvalidOperationException("The CSV file contains headers only.");

            string sourceFolderPath = Path.GetDirectoryName(csvPath)
                ?? throw new InvalidOperationException("The metric file has no parent folder.");
            string initialName = Path.GetFileName(sourceFolderPath);
            string uniqueName = BuildUniqueDatasetName(initialName);
            OxyColor displayColor = DatasetPalette[_nextColorIndex % DatasetPalette.Length];
            _nextColorIndex++;

            return new ImportedMetricDatasetViewModel(
                uniqueName,
                sourceFolderPath,
                csvPath,
                displayColor,
                iterationValues,
                numericColumns.ToDictionary(kv => kv.Key,
                                            kv => (IReadOnlyList<double?>)kv.Value,
                                            StringComparer.OrdinalIgnoreCase),
                tableColumns,
                tableRows);
        }

        private void OnImportedDatasetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (e.OldItems != null)
                {
                    foreach (var dataset in e.OldItems.OfType<ImportedMetricDatasetViewModel>())
                        dataset.PropertyChanged -= OnDatasetPropertyChanged;
                }

                if (e.NewItems != null)
                {
                    foreach (var dataset in e.NewItems.OfType<ImportedMetricDatasetViewModel>())
                        dataset.PropertyChanged += OnDatasetPropertyChanged;
                }

                if (SelectedDataset != null && !ImportedDatasets.Contains(SelectedDataset))
                    SelectedDataset = ImportedDatasets.FirstOrDefault();

                UpdateAvailableMetrics();

                foreach (var plot in Plots)
                    plot.SyncDatasets(ImportedDatasets);

                HasImportedDatasets = ImportedDatasets.Count > 0;
                ImportSummary = HasImportedDatasets
                    ? $"{ImportedDatasets.Count} dataset(s) loaded"
                    : "No iteration metrics loaded";
            }
            catch (Exception ex)
            {
                HandleUnexpectedException("Dataset refresh failed", ex);
            }
        }

        private void OnPlotsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (SelectedPlot == null && Plots.Count > 0)
                    SelectedPlot = Plots[0];

                if (SelectedPlot != null && !Plots.Contains(SelectedPlot))
                    SelectedPlot = Plots.FirstOrDefault();

                PlotGridSpan = Plots.Count <= 1 ? 1 : 2;
                PlotSummary = Plots.Count == 1 ? "1 plot available" : $"{Plots.Count} plots available";
                CanAddPlot = Plots.Count < MaxPlots;
                CanRemoveSelectedPlot = SelectedPlot != null && Plots.Count > 1;
                OnPropertyChanged(nameof(PlotCount));
                OnPropertyChanged(nameof(PlotSlot1));
                OnPropertyChanged(nameof(PlotSlot2));
                OnPropertyChanged(nameof(PlotSlot3));
                OnPropertyChanged(nameof(PlotSlot4));
                OnPropertyChanged(nameof(HasPlotSlot1));
                OnPropertyChanged(nameof(HasPlotSlot2));
                OnPropertyChanged(nameof(HasPlotSlot3));
                OnPropertyChanged(nameof(HasPlotSlot4));
                OnPropertyChanged(nameof(UseSinglePlotLayout));
                OnPropertyChanged(nameof(UseTwoPlotLayout));
                OnPropertyChanged(nameof(UseQuadPlotLayout));
                OnPropertyChanged(nameof(HasVisiblePlots));
                ApplyDefaultCanvasLayout();
            }
            catch (Exception ex)
            {
                HandleUnexpectedException("Plot layout refresh failed", ex);
            }
        }

        private void OnDatasetPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ImportedMetricDatasetViewModel.Name))
                return;

            try
            {
                foreach (var plot in Plots)
                    plot.RefreshPlotModel();
            }
            catch (Exception ex)
            {
                HandleUnexpectedException("Dataset rename refresh failed", ex);
            }
        }

        private void OnPlotStatusChanged(object? sender, string status)
        {
            if (!string.IsNullOrWhiteSpace(status))
                StatusMessage = status;
        }

        private void ResetAvailableMetrics()
        {
            AvailableMetrics.Clear();
            foreach (var metric in PreferredMetricOrder)
                AvailableMetrics.Add(metric);
        }

        private void UpdateAvailableMetrics()
        {
            var importedMetrics = ImportedDatasets
                .SelectMany(dataset => dataset.NumericColumns.Keys)
                .Where(metric => !string.Equals(metric, IterationColumnName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var orderedMetrics = PreferredMetricOrder
                .Where(metric => importedMetrics.Contains(metric, StringComparer.OrdinalIgnoreCase))
                .Concat(importedMetrics.Where(metric => !PreferredMetricOrder.Contains(metric, StringComparer.OrdinalIgnoreCase))
                                       .OrderBy(metric => metric, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (orderedMetrics.Count == 0)
                orderedMetrics = PreferredMetricOrder.ToList();

            AvailableMetrics.Clear();
            foreach (var metric in orderedMetrics)
                AvailableMetrics.Add(metric);

            foreach (var plot in Plots)
            {
                if (!AvailableMetrics.Contains(plot.SelectedMetric, StringComparer.OrdinalIgnoreCase))
                    plot.SelectedMetric = ResolveDefaultMetric();

                plot.RefreshPlotModel();
            }
        }

        private string BuildUniqueDatasetName(string baseName)
        {
            string candidate = string.IsNullOrWhiteSpace(baseName) ? "Dataset" : baseName.Trim();
            if (!ImportedDatasets.Any(dataset => string.Equals(dataset.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;

            int suffix = 2;
            while (ImportedDatasets.Any(dataset =>
                string.Equals(dataset.Name, $"{candidate} ({suffix})", StringComparison.OrdinalIgnoreCase)))
            {
                suffix++;
            }

            return $"{candidate} ({suffix})";
        }

        private string ResolveDefaultMetric()
            => AvailableMetrics.FirstOrDefault(metric => string.Equals(metric, DefaultMetric, StringComparison.OrdinalIgnoreCase))
               ?? AvailableMetrics.FirstOrDefault()
               ?? DefaultMetric;

        private string ResolveMetricForNewPlot()
        {
            if (AvailableMetrics.Count == 0)
                return DefaultMetric;

            if (SelectedPlot == null)
                return AvailableMetrics[Math.Min(Plots.Count, AvailableMetrics.Count - 1)];

            int currentIndex = AvailableMetrics
                .Select((metric, index) => new { metric, index })
                .FirstOrDefault(item => string.Equals(item.metric, SelectedPlot.SelectedMetric, StringComparison.OrdinalIgnoreCase))
                ?.index ?? -1;

            if (currentIndex < 0)
                return ResolveDefaultMetric();

            return AvailableMetrics[(currentIndex + 1) % AvailableMetrics.Count];
        }

        private AnalysisPlotViewModel? GetPlotAt(int index)
            => index >= 0 && index < Plots.Count ? Plots[index] : null;

        public void ApplyCanvasSnap(AnalysisPlotViewModel plot, PlotCanvasSnapOption snapOption)
        {
            if (!Plots.Contains(plot))
                return;

            plot.IsMinimized = false;
            SelectedPlot = plot;

            var targetLayout = ResolveSnapLayout(snapOption);
            var occupiedCells = BuildOccupiedCellSet(targetLayout);

            plot.ApplyCanvasPlacement(targetLayout.row, targetLayout.column, targetLayout.rowSpan, targetLayout.columnSpan, true);

            var freeCells = EnumerateAllGridCells()
                .Where(cell => !occupiedCells.Contains(cell))
                .ToList();

            foreach (var otherPlot in Plots.Where(candidate => !ReferenceEquals(candidate, plot)))
            {
                if (otherPlot.IsMinimized)
                {
                    otherPlot.ApplyCanvasPlacement(otherPlot.CanvasRow, otherPlot.CanvasColumn, otherPlot.CanvasRowSpan, otherPlot.CanvasColumnSpan, false);
                    continue;
                }

                if (freeCells.Count == 0)
                {
                    otherPlot.IsMinimized = true;
                    otherPlot.ApplyCanvasPlacement(otherPlot.CanvasRow, otherPlot.CanvasColumn, otherPlot.CanvasRowSpan, otherPlot.CanvasColumnSpan, false);
                    continue;
                }

                var nextCell = freeCells[0];
                freeCells.RemoveAt(0);
                otherPlot.ApplyCanvasPlacement(nextCell.row, nextCell.column, 1, 1, true);
            }

            OnPropertyChanged(nameof(HasVisiblePlots));
            StatusMessage = $"{plot.Title} snapped to {DescribeSnapOption(snapOption)}.";
        }

        private void ApplyDefaultCanvasLayout()
        {
            var visiblePlots = Plots.Where(plot => !plot.IsMinimized).ToList();

            if (visiblePlots.Count == 0)
            {
                foreach (var plot in Plots)
                    plot.ApplyCanvasPlacement(plot.CanvasRow, plot.CanvasColumn, plot.CanvasRowSpan, plot.CanvasColumnSpan, false);

                OnPropertyChanged(nameof(HasVisiblePlots));
                return;
            }

            if (visiblePlots.Count == 1)
            {
                visiblePlots[0].ApplyCanvasPlacement(0, 0, 2, 2, true);
            }
            else if (visiblePlots.Count == 2)
            {
                visiblePlots[0].ApplyCanvasPlacement(0, 0, 2, 1, true);
                visiblePlots[1].ApplyCanvasPlacement(0, 1, 2, 1, true);
            }
            else
            {
                var targetCells = new (int row, int column)[]
                {
                    (0, 0),
                    (0, 1),
                    (1, 0),
                    (1, 1)
                };

                for (int index = 0; index < visiblePlots.Count; index++)
                {
                    var targetCell = targetCells[Math.Min(index, targetCells.Length - 1)];
                    visiblePlots[index].ApplyCanvasPlacement(targetCell.row, targetCell.column, 1, 1, true);
                }
            }

            foreach (var plot in Plots.Except(visiblePlots))
                plot.ApplyCanvasPlacement(plot.CanvasRow, plot.CanvasColumn, plot.CanvasRowSpan, plot.CanvasColumnSpan, false);

            OnPropertyChanged(nameof(HasVisiblePlots));
        }

        private static (int row, int column, int rowSpan, int columnSpan) ResolveSnapLayout(PlotCanvasSnapOption snapOption)
            => snapOption switch
            {
                PlotCanvasSnapOption.TopLeft => (0, 0, 1, 1),
                PlotCanvasSnapOption.TopRight => (0, 1, 1, 1),
                PlotCanvasSnapOption.BottomLeft => (1, 0, 1, 1),
                PlotCanvasSnapOption.BottomRight => (1, 1, 1, 1),
                PlotCanvasSnapOption.TopRow => (0, 0, 1, 2),
                PlotCanvasSnapOption.BottomRow => (1, 0, 1, 2),
                PlotCanvasSnapOption.LeftColumn => (0, 0, 2, 1),
                PlotCanvasSnapOption.RightColumn => (0, 1, 2, 1),
                _ => (0, 0, 2, 2)
            };

        private static HashSet<(int row, int column)> BuildOccupiedCellSet((int row, int column, int rowSpan, int columnSpan) layout)
        {
            var cells = new HashSet<(int row, int column)>();
            for (int row = layout.row; row < layout.row + layout.rowSpan; row++)
            {
                for (int column = layout.column; column < layout.column + layout.columnSpan; column++)
                    cells.Add((row, column));
            }

            return cells;
        }

        private static IEnumerable<(int row, int column)> EnumerateAllGridCells()
        {
            yield return (0, 0);
            yield return (0, 1);
            yield return (1, 0);
            yield return (1, 1);
        }

        private static string DescribeSnapOption(PlotCanvasSnapOption snapOption)
            => snapOption switch
            {
                PlotCanvasSnapOption.TopLeft => "the top-left cell",
                PlotCanvasSnapOption.TopRight => "the top-right cell",
                PlotCanvasSnapOption.BottomLeft => "the bottom-left cell",
                PlotCanvasSnapOption.BottomRight => "the bottom-right cell",
                PlotCanvasSnapOption.TopRow => "the top row",
                PlotCanvasSnapOption.BottomRow => "the bottom row",
                PlotCanvasSnapOption.LeftColumn => "the left column",
                PlotCanvasSnapOption.RightColumn => "the right column",
                _ => "the full grid"
            };

        private void HandleUnexpectedException(string context, Exception ex)
        {
            StatusMessage = $"{context}: {ex.Message}";
            Debug.WriteLine($"{context}: {ex}");
        }

        private static IEnumerable<string> FindMetricFiles(string inputPath, bool recursiveSearch, out int skippedLocationCount)
        {
            skippedLocationCount = 0;
            var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(inputPath))
            {
                if (string.Equals(Path.GetFileName(inputPath), MetricsFileName, StringComparison.OrdinalIgnoreCase))
                    discoveredPaths.Add(inputPath);

                return discoveredPaths;
            }

            if (!Directory.Exists(inputPath))
                return discoveredPaths;

            string directPath = Path.Combine(inputPath, MetricsFileName);
            if (File.Exists(directPath))
                discoveredPaths.Add(directPath);

            bool searchDescendants = recursiveSearch || discoveredPaths.Count == 0;
            if (!searchDescendants)
                return discoveredPaths;

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(inputPath);

            while (pendingDirectories.Count > 0)
            {
                string currentDirectory = pendingDirectories.Pop();

                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(currentDirectory, MetricsFileName, SearchOption.TopDirectoryOnly))
                        discoveredPaths.Add(filePath);
                }
                catch (Exception ex) when (IsDirectoryAccessException(ex))
                {
                    skippedLocationCount++;
                    continue;
                }

                try
                {
                    foreach (var childDirectory in Directory.EnumerateDirectories(currentDirectory))
                        pendingDirectories.Push(childDirectory);
                }
                catch (Exception ex) when (IsDirectoryAccessException(ex))
                {
                    skippedLocationCount++;
                }
            }

            return discoveredPaths;
        }

        private static bool IsDirectoryAccessException(Exception exception)
            => exception is UnauthorizedAccessException
               or DirectoryNotFoundException
               or PathTooLongException
               or IOException;

        private static bool IsIterationColumn(string header)
        {
            if (string.IsNullOrWhiteSpace(header))
                return false;

            string normalizedHeader = NormalizeHeader(header);
            return IterationColumnAliases.Any(alias =>
                string.Equals(normalizedHeader, NormalizeHeader(alias), StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeHeader(string value)
            => value.Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Trim();

        private static string CleanHeader(string value)
            => value.Trim().Trim('\uFEFF');

        private static List<string> BuildUniqueHeaders(IReadOnlyList<string> rawHeaders)
        {
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var uniqueHeaders = new List<string>(rawHeaders.Count);

            for (int index = 0; index < rawHeaders.Count; index++)
            {
                string header = string.IsNullOrWhiteSpace(rawHeaders[index])
                    ? $"Column {index + 1}"
                    : rawHeaders[index];

                if (!counters.TryAdd(header, 1))
                {
                    counters[header]++;
                    uniqueHeaders.Add($"{header} ({counters[header]})");
                    continue;
                }

                uniqueHeaders.Add(header);
            }

            return uniqueHeaders;
        }

        private static bool TryParseNullableDouble(string value, out double? result)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                result = null;
                return true;
            }

            if (string.Equals(value, "Infinity", StringComparison.OrdinalIgnoreCase))
            {
                result = double.PositiveInfinity;
                return true;
            }

            if (string.Equals(value, "-Infinity", StringComparison.OrdinalIgnoreCase))
            {
                result = double.NegativeInfinity;
                return true;
            }

            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
            {
                result = parsed;
                return true;
            }

            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed))
            {
                result = parsed;
                return true;
            }

            string normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (normalized.Count(character => character == ',') == 1 && !normalized.Contains('.', StringComparison.Ordinal))
            {
                if (double.TryParse(normalized.Replace(',', '.'),
                                    NumberStyles.Float | NumberStyles.AllowThousands,
                                    CultureInfo.InvariantCulture,
                                    out parsed))
                {
                    result = parsed;
                    return true;
                }
            }

            result = null;
            return false;
        }

        private static char DetectDelimiter(string line)
        {
            int commaCount = line.Count(character => character == ',');
            int semicolonCount = line.Count(character => character == ';');
            int tabCount = line.Count(character => character == '\t');

            if (semicolonCount > commaCount && semicolonCount >= tabCount)
                return ';';

            if (tabCount > commaCount)
                return '\t';

            return ',';
        }

        private static IEnumerable<string> SplitDelimitedLine(string line, char delimiter)
        {
            if (string.IsNullOrEmpty(line))
                return [];

            var values = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '"')
                {
                    if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    continue;
                }

                if (character == delimiter && !inQuotes)
                {
                    values.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(character);
            }

            values.Add(current.ToString().Trim());
            return values;
        }

        private static async Task<string?> PickFolderAsync(string title)
        {
#if WINDOWS
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");

            var window = MauiApplication.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as WinUiWindow;
            if (window != null)
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
#else
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = title,
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".csv" } },
                    { DevicePlatform.Android, new[] { "text/csv" } },
                    { DevicePlatform.iOS, new[] { "public.comma-separated-values-text" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.comma-separated-values-text" } }
                })
            });

            return file == null ? null : Path.GetDirectoryName(file.FullPath);
#endif
        }
    }

    public sealed partial class ImportedMetricDatasetViewModel : ObservableObject
    {
        public ImportedMetricDatasetViewModel(
            string name,
            string sourceFolderPath,
            string metricsFilePath,
            OxyColor displayColor,
            IReadOnlyList<double> iterationValues,
            IReadOnlyDictionary<string, IReadOnlyList<double?>> numericColumns,
            ObservableCollection<MetricTableColumnViewModel> tableColumns,
            ObservableCollection<MetricTableRowViewModel> tableRows)
        {
            Name = name;
            SourceFolderPath = sourceFolderPath;
            MetricsFilePath = metricsFilePath;
            DisplayColor = displayColor;
            IterationValues = iterationValues;
            NumericColumns = numericColumns;
            TableColumns = tableColumns;
            TableRows = tableRows;
        }

        [ObservableProperty]
        private string name;

        public string SourceFolderPath { get; }
        public string MetricsFilePath { get; }
        public OxyColor DisplayColor { get; }
        public string ColorHex => DisplayColor.ToString();
        public Color AccentColor => Color.FromArgb(ColorHex);
        public IReadOnlyList<double> IterationValues { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<double?>> NumericColumns { get; }
        public ObservableCollection<MetricTableColumnViewModel> TableColumns { get; }
        public ObservableCollection<MetricTableRowViewModel> TableRows { get; }
        public string SourceFolderName => Path.GetFileName(SourceFolderPath);
        public string Summary => $"{TableRows.Count} iterations";

        public bool HasMetric(string metric)
            => NumericColumns.TryGetValue(metric, out var values) && values.Any(value => value.HasValue);
    }

    public sealed partial class AnalysisPlotViewModel : ObservableObject
    {
        private const string DefaultXAxisTitle = "Iteration step";
        private const string DefaultMetricName = "Residual";
        private static readonly OxyColor PlotBackground = OxyColor.Parse("#1A1A24");
        private static readonly OxyColor PlotAreaBorder = OxyColor.Parse("#444444");
        private static readonly OxyColor PlotText = OxyColor.Parse("#E0E0E0");
        private static readonly OxyColor PlotMutedText = OxyColor.Parse("#AAAAAA");
        private static readonly OxyColor PlotGrid = OxyColor.Parse("#334455");
        private static readonly OxyColor PlotMinorGrid = OxyColor.Parse("#223344");
        private static readonly OxyColor PlotLegendBackground = OxyColor.Parse("#D91E1E2E");

        private readonly int _plotIndex;
        private bool _isSynchronizingSelections;
        private string _lastAutoTitle = string.Empty;
        private string _lastAutoYAxisTitle = string.Empty;

        public AnalysisPlotViewModel(int plotIndex, string metric)
        {
            _plotIndex = plotIndex;
            DatasetSelections.CollectionChanged += OnDatasetSelectionsCollectionChanged;

            var resolvedMetric = string.IsNullOrWhiteSpace(metric) ? DefaultMetricName : metric;
            XAxisTitle = DefaultXAxisTitle;
            YAxisTitle = resolvedMetric;
            SelectedMetric = resolvedMetric;
            ApplyMetricDefaults(resolvedMetric);
            PlotModel = CreatePlotModel();
        }

        public ObservableCollection<PlotDatasetSelectionViewModel> DatasetSelections { get; } = [];

        [ObservableProperty]
        private PlotModel plotModel;

        [ObservableProperty]
        private string title = string.Empty;

        [ObservableProperty]
        private string selectedMetric = DefaultMetricName;

        [ObservableProperty]
        private string xAxisTitle = DefaultXAxisTitle;

        [ObservableProperty]
        private string yAxisTitle = DefaultMetricName;

        [ObservableProperty]
        private bool showLegend = true;

        [ObservableProperty]
        private bool showMarkers;

        [ObservableProperty]
        private bool useLogarithmicYAxis;

        [ObservableProperty]
        private bool autoScaleYAxis = true;

        [ObservableProperty]
        private string yAxisMinimumText = string.Empty;

        [ObservableProperty]
        private string yAxisMaximumText = string.Empty;

        [ObservableProperty]
        private double lineThickness = 2.0;

        [ObservableProperty]
        private string plotStatus = "Import iteration metrics to populate this chart.";

        [ObservableProperty]
        private bool isSelected;

        [ObservableProperty]
        private bool isMinimized;

        [ObservableProperty]
        private bool isVisibleOnCanvas = true;

        [ObservableProperty]
        private int canvasRow;

        [ObservableProperty]
        private int canvasColumn;

        [ObservableProperty]
        private int canvasRowSpan = 1;

        [ObservableProperty]
        private int canvasColumnSpan = 1;

        public event EventHandler<string>? StatusChanged;

        public string PlotBadge => SelectedMetric;
        public string LineSummary => $"{VisibleSelectionCount}/{PostAnalysisPageViewModel.MaxLinesPerPlot} lines";
        public string WindowButtonTitle => IsMinimized ? $"{Title} [minimized]" : Title;
        private int VisibleSelectionCount => DatasetSelections.Count(selection => selection.IsSelected);

        partial void OnTitleChanged(string value)
        {
            OnPropertyChanged(nameof(WindowButtonTitle));
            RefreshPlotModel();
        }
        partial void OnXAxisTitleChanged(string value) => RefreshPlotModel();
        partial void OnYAxisTitleChanged(string value) => RefreshPlotModel();
        partial void OnShowLegendChanged(bool value) => RefreshPlotModel();
        partial void OnShowMarkersChanged(bool value) => RefreshPlotModel();
        partial void OnUseLogarithmicYAxisChanged(bool value) => RefreshPlotModel();
        partial void OnAutoScaleYAxisChanged(bool value) => RefreshPlotModel();
        partial void OnYAxisMinimumTextChanged(string value) => RefreshPlotModel();
        partial void OnYAxisMaximumTextChanged(string value) => RefreshPlotModel();
        partial void OnLineThicknessChanged(double value) => RefreshPlotModel();
        partial void OnIsMinimizedChanged(bool value) => OnPropertyChanged(nameof(WindowButtonTitle));

        partial void OnSelectedMetricChanged(string value)
        {
            ApplyMetricDefaults(value);
            OnPropertyChanged(nameof(PlotBadge));
            RefreshPlotModel();
        }

        public void SyncDatasets(IEnumerable<ImportedMetricDatasetViewModel> datasets)
        {
            var orderedDatasets = datasets.ToList();
            var selectionByPath = DatasetSelections.ToDictionary(selection => selection.Dataset.MetricsFilePath,
                                                                 StringComparer.OrdinalIgnoreCase);

            foreach (var selection in DatasetSelections.ToList())
            {
                if (orderedDatasets.All(dataset => !string.Equals(dataset.MetricsFilePath,
                                                                  selection.Dataset.MetricsFilePath,
                                                                  StringComparison.OrdinalIgnoreCase)))
                {
                    selection.PropertyChanged -= OnSelectionPropertyChanged;
                    selection.Dispose();
                    DatasetSelections.Remove(selection);
                }
            }

            for (int index = 0; index < orderedDatasets.Count; index++)
            {
                var dataset = orderedDatasets[index];
                if (!selectionByPath.TryGetValue(dataset.MetricsFilePath, out var existingSelection))
                {
                    existingSelection = new PlotDatasetSelectionViewModel(dataset);
                    existingSelection.PropertyChanged += OnSelectionPropertyChanged;
                    DatasetSelections.Insert(Math.Min(index, DatasetSelections.Count), existingSelection);
                    continue;
                }

                int currentIndex = DatasetSelections.IndexOf(existingSelection);
                if (currentIndex >= 0 && currentIndex != index)
                    DatasetSelections.Move(currentIndex, index);
            }

            RefreshPlotModel();
        }

        public void AutoSelectDatasets(IEnumerable<ImportedMetricDatasetViewModel> datasets)
        {
            var targetPaths = datasets
                .Select(dataset => dataset.MetricsFilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _isSynchronizingSelections = true;
            try
            {
                foreach (var selection in DatasetSelections)
                {
                    if (VisibleSelectionCount >= PostAnalysisPageViewModel.MaxLinesPerPlot)
                        break;

                    if (!selection.IsSelected &&
                        targetPaths.Contains(selection.Dataset.MetricsFilePath))
                    {
                        selection.IsSelected = true;
                    }
                }
            }
            finally
            {
                _isSynchronizingSelections = false;
            }

            RefreshPlotModel();
        }

        public void SelectFirstAvailableDatasets()
        {
            _isSynchronizingSelections = true;
            try
            {
                int selectedCount = 0;
                foreach (var selection in DatasetSelections)
                {
                    bool shouldSelect = selectedCount < PostAnalysisPageViewModel.MaxLinesPerPlot;
                    selection.IsSelected = shouldSelect;
                    if (shouldSelect)
                        selectedCount++;
                }
            }
            finally
            {
                _isSynchronizingSelections = false;
            }

            RefreshPlotModel();
            RaiseStatus($"Selected {VisibleSelectionCount} line(s) for {SelectedMetric}.");
        }

        public void ClearVisibleSelections()
        {
            _isSynchronizingSelections = true;
            try
            {
                foreach (var selection in DatasetSelections)
                    selection.IsSelected = false;
            }
            finally
            {
                _isSynchronizingSelections = false;
            }

            RefreshPlotModel();
        }

        public void CopyVisibleSelectionsFrom(AnalysisPlotViewModel source)
        {
            var visiblePaths = source.DatasetSelections
                .Where(selection => selection.IsSelected)
                .Select(selection => selection.Dataset.MetricsFilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _isSynchronizingSelections = true;
            try
            {
                int selectedCount = 0;
                foreach (var selection in DatasetSelections)
                {
                    bool shouldSelect = visiblePaths.Contains(selection.Dataset.MetricsFilePath)
                        && selectedCount < PostAnalysisPageViewModel.MaxLinesPerPlot;
                    selection.IsSelected = shouldSelect;
                    if (shouldSelect)
                        selectedCount++;
                }
            }
            finally
            {
                _isSynchronizingSelections = false;
            }

            RefreshPlotModel();
        }

        public void RefreshPlotModel()
        {
            try
            {
                PlotModel = CreatePlotModel();
            }
            catch (Exception ex)
            {
                PlotStatus = $"Plot update failed: {ex.Message}";
                PlotModel = CreateFallbackPlotModel(PlotStatus);
                Debug.WriteLine($"Post-analysis plot refresh failed: {ex}");
                StatusChanged?.Invoke(this, PlotStatus);
            }

            OnPropertyChanged(nameof(LineSummary));
        }

        public void ApplyCanvasPlacement(int row, int column, int rowSpan, int columnSpan, bool isVisible)
        {
            CanvasRow = row;
            CanvasColumn = column;
            CanvasRowSpan = rowSpan;
            CanvasColumnSpan = columnSpan;
            IsVisibleOnCanvas = isVisible && !IsMinimized;
        }

        private void OnDatasetSelectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(LineSummary));
        }

        private void OnSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PlotDatasetSelectionViewModel.IsSelected))
                return;

            if (_isSynchronizingSelections)
                return;

            if (sender is not PlotDatasetSelectionViewModel selection)
                return;

            if (selection.IsSelected && VisibleSelectionCount > PostAnalysisPageViewModel.MaxLinesPerPlot)
            {
                _isSynchronizingSelections = true;
                selection.IsSelected = false;
                _isSynchronizingSelections = false;
                RaiseStatus($"A single plot can display at most {PostAnalysisPageViewModel.MaxLinesPerPlot} lines.");
                return;
            }

            RefreshPlotModel();
        }

        private void ApplyMetricDefaults(string metric)
        {
            if (string.IsNullOrWhiteSpace(metric))
                metric = DefaultMetricName;

            string autoTitle = $"Plot {_plotIndex}: {metric}";
            string autoYAxisTitle = metric;

            if (string.IsNullOrWhiteSpace(Title) || Title == _lastAutoTitle)
                Title = autoTitle;

            if (string.IsNullOrWhiteSpace(XAxisTitle))
                XAxisTitle = DefaultXAxisTitle;

            if (string.IsNullOrWhiteSpace(YAxisTitle) || YAxisTitle == _lastAutoYAxisTitle)
                YAxisTitle = autoYAxisTitle;

            _lastAutoTitle = autoTitle;
            _lastAutoYAxisTitle = autoYAxisTitle;
        }

        private PlotModel CreatePlotModel()
        {
            var model = new PlotModel
            {
                Title = Title,
                Background = PlotBackground,
                PlotAreaBorderColor = PlotAreaBorder,
                TitleColor = PlotText,
                TextColor = PlotText,
                SubtitleColor = PlotMutedText,
                IsLegendVisible = ShowLegend,
                PlotMargins = new OxyThickness(48, 22, 20, 40),
                DefaultFont = "SF Pro Text",
                TitleFont = "SF Pro Text",
                Subtitle = string.Empty
            };

            Axis yAxis = UseLogarithmicYAxis
                ? CreateLogarithmicAxis()
                : CreateLinearAxis();

            var xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = string.IsNullOrWhiteSpace(XAxisTitle) ? DefaultXAxisTitle : XAxisTitle,
                TitleColor = PlotText,
                TextColor = PlotText,
                AxislineColor = PlotAreaBorder,
                TicklineColor = PlotAreaBorder,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = PlotGrid,
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = PlotMinorGrid,
                MinorTicklineColor = PlotAreaBorder,
                Font = "SF Pro Text",
                TitleFont = "SF Pro Text",
                StringFormat = "0",
                MinimumPadding = 0.02,
                MaximumPadding = 0.02
            };

            if (!AutoScaleYAxis && TryParseAxisBounds(out var minimum, out var maximum))
            {
                yAxis.Minimum = minimum;
                yAxis.Maximum = maximum;
            }

            model.Axes.Add(xAxis);
            model.Axes.Add(yAxis);

            int renderedSeries = 0;
            int unavailableSeries = 0;

            foreach (var selection in DatasetSelections.Where(item => item.IsSelected))
            {
                if (!selection.Dataset.NumericColumns.TryGetValue(SelectedMetric, out var values))
                {
                    unavailableSeries++;
                    continue;
                }

                var lineSeries = new LineSeries
                {
                    Title = selection.DisplayName,
                    Color = selection.Dataset.DisplayColor,
                    StrokeThickness = Math.Max(1.0, LineThickness),
                    MarkerType = ShowMarkers ? MarkerType.Circle : MarkerType.None,
                    MarkerSize = ShowMarkers ? 3.0 : 0.0,
                    MarkerFill = selection.Dataset.DisplayColor,
                    CanTrackerInterpolatePoints = false,
                    Font = "SF Pro Text"
                };

                int pointCount = Math.Min(selection.Dataset.IterationValues.Count, values.Count);
                for (int index = 0; index < pointCount; index++)
                {
                    var value = values[index];
                    if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                        continue;

                    if (UseLogarithmicYAxis && value.Value <= 0)
                        continue;

                    lineSeries.Points.Add(new DataPoint(selection.Dataset.IterationValues[index], value.Value));
                }

                if (lineSeries.Points.Count == 0)
                {
                    unavailableSeries++;
                    continue;
                }

                model.Series.Add(lineSeries);
                renderedSeries++;
            }

            PlotStatus = renderedSeries switch
            {
                > 0 when unavailableSeries > 0 => $"{renderedSeries} line(s) shown. {unavailableSeries} selected dataset(s) had no plottable {SelectedMetric} values.",
                > 0 => $"{renderedSeries} line(s) shown for {SelectedMetric}.",
                _ when DatasetSelections.Count == 0 => "Import iteration metrics to populate this chart.",
                _ when VisibleSelectionCount == 0 => $"Select up to {PostAnalysisPageViewModel.MaxLinesPerPlot} datasets for this plot.",
                _ => $"No plottable {SelectedMetric} values are available for the selected datasets."
            };

            model.Subtitle = PlotStatus;
            return model;
        }

        private PlotModel CreateFallbackPlotModel(string status)
        {
            var model = new PlotModel
            {
                Title = Title,
                Background = PlotBackground,
                PlotAreaBorderColor = PlotAreaBorder,
                TitleColor = PlotText,
                TextColor = PlotText,
                SubtitleColor = PlotMutedText,
                Subtitle = status,
                DefaultFont = "SF Pro Text",
                TitleFont = "SF Pro Text"
            };

            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = string.IsNullOrWhiteSpace(XAxisTitle) ? DefaultXAxisTitle : XAxisTitle,
                TitleColor = PlotText,
                TextColor = PlotText
            });

            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = string.IsNullOrWhiteSpace(YAxisTitle) ? SelectedMetric : YAxisTitle,
                TitleColor = PlotText,
                TextColor = PlotText
            });

            return model;
        }

        private Axis CreateLinearAxis()
            => new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = string.IsNullOrWhiteSpace(YAxisTitle) ? SelectedMetric : YAxisTitle,
                TitleColor = PlotText,
                TextColor = PlotText,
                AxislineColor = PlotAreaBorder,
                TicklineColor = PlotAreaBorder,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = PlotGrid,
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = PlotMinorGrid,
                MinorTicklineColor = PlotAreaBorder,
                Font = "SF Pro Text",
                TitleFont = "SF Pro Text",
                MinimumPadding = 0.05,
                MaximumPadding = 0.05
            };

        private Axis CreateLogarithmicAxis()
            => new LogarithmicAxis
            {
                Position = AxisPosition.Left,
                Title = string.IsNullOrWhiteSpace(YAxisTitle) ? SelectedMetric : YAxisTitle,
                TitleColor = PlotText,
                TextColor = PlotText,
                AxislineColor = PlotAreaBorder,
                TicklineColor = PlotAreaBorder,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = PlotGrid,
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = PlotMinorGrid,
                MinorTicklineColor = PlotAreaBorder,
                Font = "SF Pro Text",
                TitleFont = "SF Pro Text",
                Base = 10,
                MinimumPadding = 0.05,
                MaximumPadding = 0.05
            };

        private bool TryParseAxisBounds(out double minimum, out double maximum)
        {
            minimum = 0.0;
            maximum = 0.0;

            if (!double.TryParse(YAxisMinimumText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out minimum))
                return false;

            if (!double.TryParse(YAxisMaximumText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out maximum))
                return false;

            if (UseLogarithmicYAxis && minimum <= 0)
                return false;

            return maximum > minimum;
        }

        private void RaiseStatus(string status)
        {
            PlotStatus = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    public sealed partial class PlotDatasetSelectionViewModel : ObservableObject, IDisposable
    {
        public PlotDatasetSelectionViewModel(ImportedMetricDatasetViewModel dataset)
        {
            Dataset = dataset;
            Dataset.PropertyChanged += OnDatasetPropertyChanged;
        }

        public ImportedMetricDatasetViewModel Dataset { get; }

        [ObservableProperty]
        private bool isSelected;

        public string DisplayName => Dataset.Name;
        public string SourceFolderName => Dataset.SourceFolderName;
        public string ColorHex => Dataset.ColorHex;
        public Color AccentColor => Dataset.AccentColor;

        private void OnDatasetPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ImportedMetricDatasetViewModel.Name))
                return;

            OnPropertyChanged(nameof(DisplayName));
        }

        public void Dispose()
        {
            Dataset.PropertyChanged -= OnDatasetPropertyChanged;
        }
    }

    public sealed class MetricTableColumnViewModel
    {
        public MetricTableColumnViewModel(string title)
        {
            Title = title;
        }

        public string Title { get; }
    }

    public sealed class MetricTableRowViewModel
    {
        public MetricTableRowViewModel(ObservableCollection<MetricTableCellViewModel> cells)
        {
            Cells = cells;
        }

        public ObservableCollection<MetricTableCellViewModel> Cells { get; }
    }

    public sealed class MetricTableCellViewModel
    {
        public MetricTableCellViewModel(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }

    public readonly record struct PlotExportRequest(AnalysisPlotViewModel Plot);
    public readonly record struct PlotResizeRequest(AnalysisPlotViewModel Plot);

    public enum PlotCanvasSnapOption
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        TopRow,
        BottomRow,
        LeftColumn,
        RightColumn,
        FullGrid
    }
}
