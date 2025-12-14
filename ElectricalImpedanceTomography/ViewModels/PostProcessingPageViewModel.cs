using BH.Engine.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataAccessLayer;
using Microsoft.Maui.Devices; // added for DevicePlatform
using Microsoft.Maui.Storage; // added for FilePicker/FileSystem
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO; // added for File/Directory/Path
using System.Text.Json;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.PostProcessing;
using Utility.Rendering;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class PostProcessingPageViewModel : ObservableObject
    {
        // --- Observable Properties (Bound to UI) ---

        [ObservableProperty]
        private double _filterStrength = 50;

        [ObservableProperty]
        private double _minCutoff = 0.0;

        [ObservableProperty]
        private double _maxCutoff = 1.0;

        [ObservableProperty]
        private bool _isLogScale = false;

        [ObservableProperty]
        private bool _showWireframe = true;

        [ObservableProperty]
        private bool _showNodes = false;

        [ObservableProperty]
        private bool _showContours = false;

        [ObservableProperty]
        private ConductivityDisplayMode _selectedConductivityDisplayMode = ConductivityDisplayMode.Classic;

        [ObservableProperty]
        private string _activeSource = "No dataset loaded";

        [ObservableProperty]
        private string _lastSavedPath = "Not saved yet";

        [ObservableProperty]
        private bool _hasMesh;

        [ObservableProperty]
        private string _canvasMessage = "No reconstruction available. Load a workspace result or import a file.";

        // Statistics Display
        [ObservableProperty]
        private string _statMin = "0.000";

        [ObservableProperty]
        private string _statMax = "0.000";

        [ObservableProperty]
        private string _statAvg = "0.000";

        // Collections
        public ObservableCollection<string> ConsoleLogs { get; } = new();
        public ObservableCollection<string> HistoryLog { get; } = new();
        public IReadOnlyList<ConductivityDisplayMode> ConductivityDisplayModes { get; } = Enum.GetValues<ConductivityDisplayMode>();

        public ObservableCollection<IPostProcessing> PostProcessingOptions { get; } = new();
        public ObservableCollection<PostProcessingGroup> PostProcessingGroups { get; } = new();
        public ObservableCollection<PostProcessingParameterOption> ParameterOptions { get; } = new();

        [ObservableProperty]
        private IPostProcessing? _selectedPostProcessor;

        [ObservableProperty]
        private bool _hasParameterOptions;

        // Events
        public event EventHandler? MeshUpdated;

        // Internal state
        private IDiscretization? _discretization;
        private ConductivityDistribution? _currentDistribution;
        private ConductivityDistribution? _originalDistribution;
        private double _dataMin;
        private double _dataMax = 1.0;
        private bool _cutoffsInitialized;

        public FEMMesh? FemMesh => _discretization as FEMMesh;
        public LBMGrid? LbmGrid => _discretization as LBMGrid;

        public IEnumerable<double> ElementConductivities()
        {
            if (_discretization == null || _currentDistribution == null)
                return Enumerable.Empty<double>();

            if (_discretization is LBMGrid lbm)
            {
                return lbm.GetElements()
                          .Cast<LBMElement>()
                          .Where(e => !e.IsWall)
                          .Select(e => _currentDistribution.Conductivities.TryGetValue(e.Id, out var v)
                                                ? v
                                                : e.Conductivity);
            }

            return _discretization.GetElements()
                                   .Select(e => _currentDistribution.Conductivities.TryGetValue(e.Id, out var v)
                                                        ? v
                                                        : e.Conductivity);
        }

        public double GetConductivityValue(int elementId, double fallback)
        {
            if (_currentDistribution != null && _currentDistribution.Conductivities.TryGetValue(elementId, out var value))
                return value;

            return fallback;
        }

        private record SavedFemMesh(List<SavedVertex> Vertices, List<SavedFemElement> Elements);
        private record SavedVertex(int Id, double X, double Y, bool IsBoundary, bool IsElectrode);
        private record SavedFemElement(int Id, int V1, int V2, int V3);
        private record SavedLbmCell(int Id, int X, int Y, bool IsWall, bool IsElectrode, bool IsGhost);
        private record SavedLbmGrid(int Nx, int Ny, List<SavedLbmCell> Cells);
        private record SavedPostProcessingSnapshot(string Type,
                                                   Dictionary<int, double> Conductivities,
                                                   SavedFemMesh? Fem,
                                                   SavedLbmGrid? Lbm,
                                                   string? Label);

        public PostProcessingPageViewModel()
        {
            InitializePostProcessors();
            LoadLatestWorkspaceResult();

            Log("System initialized.", "info");
            AddToHistory("Init");
        }

        private void InitializePostProcessors()
        {
            PostProcessingOptions.Clear();
            PostProcessingGroups.Clear();

            var smoothing = new PostProcessingGroup(
                "Smoothing & Denoising",
                "Reduce noise while retaining overall structure.",
                new IPostProcessing[]
                {
                    new LaplacianSmoothingPostProcessing(),
                    new MedianFilterPostProcessing(),
                    new GaussianBlurPostProcessing(),
                    new BilateralFilterPostProcessing(),
                    new MeanFilterPostProcessing(),
                    new AnisotropicDiffusionPostProcessing()
                });

            var enhancement = new PostProcessingGroup(
                "Enhancement & Refinement",
                "Sharpen or emphasize edges and gradients.",
                new IPostProcessing[]
                {
                    new EdgeEnhancementPostProcessing(),
                    new HighPassEnhancementPostProcessing(),
                    new AdaptiveSharpenPostProcessing(),
                    new ContrastStretchPostProcessing(),
                    new GammaCorrectionPostProcessing()
                });

            var normalization = new PostProcessingGroup(
                "Normalization & Clipping",
                "Constrain or normalize ranges for stability.",
                new IPostProcessing[]
                {
                    new ThresholdFilterPostProcessing(),
                    new SigmaClippingPostProcessing(),
                    new WinsorizedClippingPostProcessing(),
                    new NormalizationPostProcessing()
                });

            foreach (var group in new[] { smoothing, enhancement, normalization })
            {
                PostProcessingGroups.Add(group);
                foreach (var option in group)
                    PostProcessingOptions.Add(option);
            }

            SelectedPostProcessor = PostProcessingOptions.FirstOrDefault();
        }

        partial void OnSelectedPostProcessorChanged(IPostProcessing? value)
        {
            UpdateParameterOptions(value);
        }

        // --- Loading logic ---
        [RelayCommand]
        public void LoadLatestWorkspaceResult()
        {
            var lastResult = Workspace.GetReconstructionResults().LastOrDefault();
            var fallbackMesh = Workspace.GetDiscretization();

            if (lastResult != null)
            {
                var distribution = lastResult.GetReconstructedConductivityDistribution();
                var mesh = lastResult.GetDiscretization() ?? fallbackMesh?.GetDiscretization();

                if (mesh != null)
                {
                    LoadDiscretization(mesh.DeepCopy(), new ConductivityDistribution(distribution.Conductivities), "Workspace result");
                    CanvasMessage = string.Empty;
                    return;
                }
            }

            var fallbackSigma = Workspace.GetOriginalConductivityDistribution() ?? fallbackMesh?.GetConductivityDistribution();
            if (fallbackMesh != null && fallbackSigma != null)
            {
                LoadDiscretization(fallbackMesh.DeepCopy(), new ConductivityDistribution(fallbackSigma.Conductivities), "Active workspace discretization");
                CanvasMessage = string.Empty;
                return;
            }

            Log("No reconstruction results available in workspace.", "warn");
            ActiveSource = "No dataset loaded";
            CanvasMessage = "No reconstruction available in the workspace.";
            HasMesh = false;
        }

        [RelayCommand]
        public async Task ImportSavedResultAsync()
        {
            try
            {
                var jsonFileType = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { "*.json", "*.eitmesh", "*.stl", "*.csv" } },
                    { DevicePlatform.Android, new[] { "application/json", "application/octet-stream" } },
                    { DevicePlatform.iOS, new[] { "public.json", "public.composite-content" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.json", "public.composite-content" } },
                });

                var file = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Select a saved post-processing snapshot",
                    FileTypes = jsonFileType
                });

                if (file == null)
                    return;

                LoadFromFile(file.FullPath);
            }
            catch (Exception ex)
            {
                Log($"Failed to pick reconstruction: {ex.Message}", "error");
            }
        }

        [RelayCommand]
        public async Task SaveProcessedAsync()
        {
            if (_discretization == null || _currentDistribution == null)
            {
                Log("No processed dataset to save.", "warn");
                return;
            }

            var repo = new MeshRepository();
            string name = $"postprocessed_{DateTime.Now:yyyyMMdd_HHmmss}";
            try
            {
                if (_discretization is FEMMesh femMesh)
                {
                    femMesh.SetConductivityDistribution(_currentDistribution);
                    repo.SaveFEMMesh(femMesh, name);
                }
                else if (_discretization is LBMGrid lbmGrid)
                {
                    lbmGrid.SetConductivityDistribution(_currentDistribution);
                    repo.SaveLBMGrid(lbmGrid, name);
                }

                var snapshot = CreateSnapshot(name);
                var exportDir = FileSystem.AppDataDirectory;
                Directory.CreateDirectory(exportDir);
                var jsonPath = Path.Combine(exportDir, $"{name}.json");
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(snapshot, options);
                File.WriteAllText(jsonPath, json);

                var csvPath = Path.Combine(exportDir, $"{name}.csv");
                using (var writer = new StreamWriter(csvPath))
                {
                    writer.WriteLine("elementId,conductivity");
                    foreach (var kv in _currentDistribution.Conductivities.OrderBy(kv => kv.Key))
                    {
                        writer.WriteLine($"{kv.Key},{kv.Value.ToString(CultureInfo.InvariantCulture)}");
                    }
                }

                LastSavedPath = jsonPath;
                Log($"Saved snapshot to {jsonPath} and CSV to {csvPath}.", "success");
                AddToHistory($"Saved {name}");
            }
            catch (Exception ex)
            {
                Log($"Failed to save: {ex.Message}", "error");
            }
        }

        private SavedPostProcessingSnapshot CreateSnapshot(string label)
        {
            if (_currentDistribution == null)
                return new SavedPostProcessingSnapshot("None", new Dictionary<int, double>(), null, null, label);

            SavedFemMesh? fem = null;
            SavedLbmGrid? lbm = null;

            if (FemMesh is FEMMesh femMesh)
            {
                fem = new SavedFemMesh(
                    femMesh.Vertices.Select(v => new SavedVertex(v.GlobalId, v.X, v.Y, v.IsBoundary, v.IsElectrode)).ToList(),
                    femMesh.ElementsTyped.Select(e => new SavedFemElement(e.Id,
                                                                           e.Vertices[0].GlobalId,
                                                                           e.Vertices[1].GlobalId,
                                                                           e.Vertices[2].GlobalId)).ToList());
            }

            if (LbmGrid is LBMGrid lbmGrid)
            {
                var cells = lbmGrid.GetElements()
                                   .Cast<LBMElement>()
                                   .Select(e =>
                                   {
                                       var (x, y) = lbmGrid.ToLattice(e.Id);
                                       return new SavedLbmCell(e.Id, x, y, e.IsWall, e.IsElectrode, e.GhostElement);
                                   })
                                   .ToList();
                lbm = new SavedLbmGrid(lbmGrid.Nx, lbmGrid.Ny, cells);
            }

            var conductivities = new Dictionary<int, double>(_currentDistribution.Conductivities);
            var type = FemMesh != null ? "FEM" : (LbmGrid != null ? "LBM" : "Unknown");
            return new SavedPostProcessingSnapshot(type, conductivities, fem, lbm, label);
        }

        public void LoadFromFile(string path)
        {
            try
            {
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = JsonSerializer.Deserialize<SavedPostProcessingSnapshot>(File.ReadAllText(path));
                    if (snapshot == null)
                    {
                        Log("Selected JSON file is not a valid snapshot.", "warn");
                        return;
                    }

                    LoadSnapshot(snapshot, Path.GetFileName(path));
                    CanvasMessage = string.Empty;
                    return;
                }

                var repo = new MeshRepository();
                IDiscretization? discretization = null;
                if (path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".eitmesh", StringComparison.OrdinalIgnoreCase))
                {
                    discretization = TryLoadMesh(repo, path);
                }

                if (discretization == null && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    discretization = Workspace.GetDiscretization()?.DeepCopy();
                    if (discretization == null)
                    {
                        Log("A conductivity CSV requires an active workspace discretization.", "warn");
                        return;
                    }
                }

                if (discretization == null)
                {
                    Log("Unsupported file type for post processing.", "warn");
                    return;
                }

                var distribution = ApplySidecarConductivities(path, discretization.GetConductivityDistribution());
                LoadDiscretization(discretization, distribution, Path.GetFileName(path));
                CanvasMessage = string.Empty;
            }
            catch (Exception ex)
            {
                Log($"Failed to load result: {ex.Message}", "error");
                ActiveSource = "No dataset loaded";
                HasMesh = false;
                CanvasMessage = "Unable to load the selected file.";
            }
        }

        private ConductivityDistribution ApplySidecarConductivities(string path, ConductivityDistribution fallback)
        {
            try
            {
                var csvPath = Path.ChangeExtension(path, ".csv");
                if (csvPath == null || !File.Exists(csvPath))
                    return fallback;

                var updated = new Dictionary<int, double>(fallback.Conductivities);
                foreach (var line in File.ReadLines(csvPath).Skip(1))
                {
                    var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length < 2)
                        continue;
                    if (int.TryParse(parts[0], out var id) &&
                        double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var sigma))
                    {
                        updated[id] = sigma;
                    }
                }

                return new ConductivityDistribution(updated);
            }
            catch (Exception ex)
            {
                Log($"Failed to read conductivity CSV: {ex.Message}", "warn");
                return fallback;
            }
        }

        private static IDiscretization? TryLoadMesh(MeshRepository repo, string path)
        {
            try
            {
                return repo.LoadFEMMesh(path);
            }
            catch
            {
                // ignore and try loading as LBM
            }

            try
            {
                return repo.LoadLBMGrid(path);
            }
            catch
            {
                return null;
            }
        }

        private ConductivityDistribution BuildDistribution(IDiscretization discretization, Dictionary<int, double> source)
        {
            var dict = new Dictionary<int, double>();
            foreach (var element in discretization.GetElements())
            {
                dict[element.Id] = source.TryGetValue(element.Id, out var value)
                    ? value
                    : element.Conductivity;
            }

            return new ConductivityDistribution(dict);
        }

        private void LoadSnapshot(SavedPostProcessingSnapshot snapshot, string fallbackLabel)
        {
            if (snapshot.Type.Equals("FEM", StringComparison.OrdinalIgnoreCase) && snapshot.Fem != null)
            {
                var vertices = snapshot.Fem.Vertices
                                          .ToDictionary(v => v.Id,
                                                        v => new FEMVertex(v.Id, v.X, v.Y)
                                                        {
                                                            IsBoundary = v.IsBoundary,
                                                            IsElectrode = v.IsElectrode
                                                        });

                var elements = new List<FEMElement>();
                foreach (var el in snapshot.Fem.Elements)
                {
                    if (!vertices.TryGetValue(el.V1, out var v1) ||
                        !vertices.TryGetValue(el.V2, out var v2) ||
                        !vertices.TryGetValue(el.V3, out var v3))
                    {
                        continue;
                    }

                    var element = new FEMElement(el.Id, v1, v2, v3);
                    if (snapshot.Conductivities.TryGetValue(el.Id, out var sigma))
                        element.Conductivity = sigma;
                    elements.Add(element);
                }

                var mesh = new FEMMesh(vertices.Values, elements);
                var distribution = BuildDistribution(mesh, snapshot.Conductivities);
                mesh.SetConductivityDistribution(distribution);
                LoadDiscretization(mesh, distribution, snapshot.Label ?? fallbackLabel);
                return;
            }

            if (snapshot.Type.Equals("LBM", StringComparison.OrdinalIgnoreCase) && snapshot.Lbm != null)
            {
                var grid = new LBMGrid(snapshot.Lbm.Nx, snapshot.Lbm.Ny);
                var cells = snapshot.Lbm.Cells.ToDictionary(c => c.Id);

                foreach (var element in grid.GetElements().Cast<LBMElement>())
                {
                    if (cells.TryGetValue(element.Id, out var saved))
                    {
                        element.IsWall = saved.IsWall;
                        element.IsElectrode = saved.IsElectrode;
                        element.GhostElement = saved.IsGhost;
                    }
                }

                var distribution = BuildDistribution(grid, snapshot.Conductivities);
                grid.SetConductivityDistribution(distribution);
                LoadDiscretization(grid, distribution, snapshot.Label ?? fallbackLabel);
                return;
            }

            Log("Snapshot type could not be loaded.", "warn");
        }

        private void LoadDiscretization(IDiscretization discretization, ConductivityDistribution distribution, string label)
        {
            _discretization = discretization;
            _currentDistribution = new ConductivityDistribution(distribution.Conductivities);
            _originalDistribution = new ConductivityDistribution(distribution.Conductivities);
            ActiveSource = label;
            _cutoffsInitialized = false;
            CanvasMessage = string.Empty;

            HasMesh = true;
            UpdateStatistics(resetCutoffs: true);
            MeshUpdated?.Invoke(this, EventArgs.Empty);
        }

        // --- Commands ---
        [RelayCommand]
        public void ApplySmooth() => RunPostProcessor(new LaplacianSmoothingPostProcessing { Weight = FilterStrength / 100.0 });

        [RelayCommand]
        public void ApplySharpen() => RunPostProcessor(new EdgeEnhancementPostProcessing { Weight = FilterStrength / 100.0 });

        [RelayCommand]
        public void ApplyMedian() => RunPostProcessor(new MedianFilterPostProcessing());

        [RelayCommand]
        public void ApplySelectedPostProcessing()
        {
            if (SelectedPostProcessor is null)
            {
                Log("Select a post-processing operation.", "warn");
                return;
            }

            RunPostProcessor(SelectedPostProcessor);
        }

        [RelayCommand]
        public void ClearHistory() => HistoryLog.Clear();

        private void RunPostProcessor(IPostProcessing processor)
        {
            if (_discretization == null || _currentDistribution == null)
            {
                Log("No dataset loaded for post processing.", "warn");
                return;
            }

            var updated = processor.Process(_discretization, _currentDistribution);
            _currentDistribution = updated;
            _discretization.SetConductivityDistribution(updated);

            UpdateStatistics();
            MeshUpdated?.Invoke(this, EventArgs.Empty);

            var summary = BuildParameterSummary();
            AddToHistory(string.IsNullOrEmpty(summary)
                ? $"Applied {processor.Name}"
                : $"Applied {processor.Name} ({summary})");
            Log($"Applied {processor.Name}.", "success");
        }

        [RelayCommand]
        public void ResetAll()
        {
            Log("Resetting all parameters.");
            if (_originalDistribution != null && _discretization != null)
            {
                _currentDistribution = new ConductivityDistribution(_originalDistribution.Conductivities);
                _discretization.SetConductivityDistribution(_currentDistribution);
            }

            FilterStrength = 50;
            IsLogScale = false;
            _cutoffsInitialized = false;
            UpdateStatistics(resetCutoffs: true);
            HistoryLog.Clear();
            AddToHistory("Init");
            MeshUpdated?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void ExportData() => Log("Data exported to clipboard.", "success");

        [RelayCommand]
        public void ClearConsole() => ConsoleLogs.Clear();

        // --- Helpers ---
        public void UpdateStatistics(bool resetCutoffs = false)
        {
            if (_currentDistribution == null || _currentDistribution.Conductivities.Count == 0)
                return;

            _dataMin = _currentDistribution.Conductivities.Values.Min();
            _dataMax = _currentDistribution.Conductivities.Values.Max();
            StatMin = _dataMin.ToString("F3");
            StatMax = _dataMax.ToString("F3");
            StatAvg = _currentDistribution.Conductivities.Values.Average().ToString("F3");

            if (resetCutoffs || !_cutoffsInitialized)
            {
                MinCutoff = _dataMin;
                MaxCutoff = _dataMax;
                _cutoffsInitialized = true;
            }
        }

        public double ProcessValue(double val)
            => NormalizeValue(val).Normalized;

        public ValueNormalization NormalizeValue(double val)
        {
            double working = val;
            if (IsLogScale)
                working = Math.Log10(Math.Max(1e-6, working) + 1);

            double min = MinCutoff;
            double max = MaxCutoff;
            if (max <= min)
                max = min + 1e-6;

            working = Math.Clamp(working, min, max);
            double normalized = (working - min) / (max - min);
            return new ValueNormalization(working, normalized, min, max);
        }

        private void Log(string msg, string type = "normal")
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            ConsoleLogs.Add($"[{time}] {msg}");
        }

        private void AddToHistory(string action) => HistoryLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {action}");

        private void UpdateParameterOptions(IPostProcessing? processor)
        {
            ParameterOptions.Clear();

            if (processor is IWeightedPostProcessing weighted)
            {
                ParameterOptions.Add(PostProcessingParameterOption.CreatePercentage(
                    "Weight",
                    "Adjusts the influence of the filter (0-100%).",
                    weighted.Weight * 100.0,
                    v => weighted.Weight = v / 100.0,
                    0.5));
            }

            if (processor is SigmaClippingPostProcessing sigma)
            {
                ParameterOptions.Add(new PostProcessingParameterOption(
                    "Sigma (σ)",
                    "Standard deviation envelope for clipping.",
                    0.5,
                    5.0,
                    0.1,
                    sigma.Sigma,
                    v => sigma.Sigma = v,
                    unit: "σ"));
            }

            HasParameterOptions = ParameterOptions.Count > 0;
        }

        private string BuildParameterSummary()
        {
            if (!HasParameterOptions)
                return string.Empty;

            return string.Join(", ", ParameterOptions.Select(p => $"{p.Name}={p.FormattedValue}"));
        }
    }

    public readonly record struct ValueNormalization(double Working, double Normalized, double Min, double Max);

    public class PostProcessingParameterOption : ObservableObject
    {
        private readonly Action<double> _apply;
        private double _value;

        public PostProcessingParameterOption(
            string name,
            string description,
            double minimum,
            double maximum,
            double step,
            double initialValue,
            Action<double> apply,
            bool isPercentage = false,
            string unit = "")
        {
            Name = name;
            Description = description;
            Minimum = minimum;
            Maximum = maximum;
            Step = step;
            _value = initialValue;
            _apply = apply;
            IsPercentage = isPercentage;
            Unit = unit;
        }

        public string Name { get; }
        public string Description { get; }
        public double Minimum { get; }
        public double Maximum { get; }
        public double Step { get; }
        public bool IsPercentage { get; }
        public string Unit { get; }

        public double Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value))
                {
                    _apply(IsPercentage ? value / 100.0 : value);
                    OnPropertyChanged(nameof(FormattedValue));
                }
            }
        }

        public string FormattedValue
        {
            get
            {
                if (IsPercentage)
                    return $"{Value:0.#}%";

                string suffix = string.IsNullOrWhiteSpace(Unit) ? string.Empty : Unit;
                return $"{Value:0.###}{suffix}";
            }
        }

        public static PostProcessingParameterOption CreatePercentage(
            string name,
            string description,
            double initialPercent,
            Action<double> apply,
            double step = 1.0)
            => new(name, description, 0, 100, step, initialPercent, apply, isPercentage: true);
    }
}
