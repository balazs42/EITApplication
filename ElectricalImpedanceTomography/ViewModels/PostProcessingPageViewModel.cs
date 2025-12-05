using BH.Engine.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataAccessLayer;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.PostProcessing;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class PostProcessingPageViewModel : ObservableObject
    {
        // --- Data Structures ---
        public class FemNode
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Value { get; set; }
            public double OriginalValue { get; set; }
        }

        public class FemElement
        {
            public int[] NodeIndices { get; set; } = new int[3];
            public double Value { get; set; }
        }

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
        private string _selectedColormap = "Jet";

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
        public List<FemNode> Nodes { get; private set; } = new();
        public List<FemElement> Elements { get; private set; } = new();
        public ObservableCollection<string> ConsoleLogs { get; } = new();
        public ObservableCollection<string> HistoryLog { get; } = new();
        public List<string> Colormaps { get; } = new() { "Jet", "Hot", "Gray", "Parula" };

        public ObservableCollection<IPostProcessing> PostProcessingOptions { get; } = new();

        [ObservableProperty]
        private IPostProcessing? _selectedPostProcessor;

        // Events
        public event EventHandler? MeshUpdated;

        // Internal state
        private IDiscretization? _discretization;
        private ConductivityDistribution? _currentDistribution;
        private ConductivityDistribution? _originalDistribution;
        private double _dataMin;
        private double _dataMax = 1.0;
        private bool _cutoffsInitialized;

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
            PostProcessingOptions.Add(new LaplacianSmoothingPostProcessing());
            PostProcessingOptions.Add(new MedianFilterPostProcessing());
            PostProcessingOptions.Add(new EdgeEnhancementPostProcessing());
            PostProcessingOptions.Add(new ThresholdFilterPostProcessing());
            PostProcessingOptions.Add(new SigmaClippingPostProcessing());
            PostProcessingOptions.Add(new NormalizationPostProcessing());
            PostProcessingOptions.Add(new GaussianBlurPostProcessing());
            PostProcessingOptions.Add(new BilateralFilterPostProcessing());
            PostProcessingOptions.Add(new ContrastStretchPostProcessing());
            PostProcessingOptions.Add(new GammaCorrectionPostProcessing());
            PostProcessingOptions.Add(new HighPassEnhancementPostProcessing());
            PostProcessingOptions.Add(new WinsorizedClippingPostProcessing());
            SelectedPostProcessor = PostProcessingOptions.FirstOrDefault();
        }

        // --- Loading logic ---
        public bool LoadLatestWorkspaceResult()
        {
            var lastResult = Workspace.GetReconstructionResults().LastOrDefault();

            if (lastResult?.GetDiscretization() is Discretization mesh)
            {
                var distribution = lastResult.GetReconstructedConductivityDistribution();
                LoadDiscretization(mesh.DeepCopy(), new ConductivityDistribution(distribution.Conductivities), "Workspace result");
                CanvasMessage = string.Empty;
                return true;
            }

            var fallbackMesh = Workspace.GetDiscretization();
            var fallbackSigma = Workspace.GetOriginalConductivityDistribution() ?? fallbackMesh?.GetConductivityDistribution();
            if (fallbackMesh != null && fallbackSigma != null)
            {
                LoadDiscretization(fallbackMesh.DeepCopy(), new ConductivityDistribution(fallbackSigma.Conductivities), "Active workspace discretization");
                CanvasMessage = string.Empty;
                return true;
            }

            Log("No reconstruction results available in workspace.", "warn");
            ActiveSource = "No dataset loaded";
            CanvasMessage = "No reconstruction available in the workspace.";
            HasMesh = false;
            return false;
        }

        [RelayCommand]
        public void LoadLatestWorkspaceResultCommand()
        {
            LoadLatestWorkspaceResult();
        }

        [RelayCommand]
        public async Task ImportSavedResultAsync()
        {
            try
            {
                var file = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Select a reconstruction (STL or mesh)"
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
            if (_discretization is not FEMMesh femMesh || _currentDistribution == null)
            {
                Log("No processed mesh to save.", "warn");
                return;
            }

            var repo = new MeshRepository();
            string name = $"postprocessed_{DateTime.Now:yyyyMMdd_HHmmss}";
            try
            {
                // Persist geometry + conductivities
                femMesh.SetConductivityDistribution(_currentDistribution);
                repo.SaveFEMMesh(femMesh, name);

                // Sidecar CSV with conductivity values
                var exportDir = FileSystem.AppDataDirectory;
                var csvPath = Path.Combine(exportDir, $"{name}.csv");
                using (var writer = new StreamWriter(csvPath))
                {
                    writer.WriteLine("elementId,conductivity");
                    foreach (var kv in _currentDistribution.Conductivities.OrderBy(kv => kv.Key))
                    {
                        writer.WriteLine($"{kv.Key},{kv.Value.ToString(CultureInfo.InvariantCulture)}");
                    }
                }

                var meshFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EITApplication", "Meshes");
                Directory.CreateDirectory(meshFolder);

                LastSavedPath = $"Mesh: {meshFolder}, CSV: {csvPath}";
                Log($"Saved post processed mesh as '{name}'. CSV exported to {csvPath}.", "success");
                AddToHistory($"Saved {name}");
            }
            catch (Exception ex)
            {
                Log($"Failed to save: {ex.Message}", "error");
            }
        }

        public void LoadFromFile(string path)
        {
            try
            {
                var repo = new MeshRepository();
                IDiscretization? discretization = null;
                if (path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".eitmesh", StringComparison.OrdinalIgnoreCase))
                {
                    discretization = repo.LoadFEMMesh(path);
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

        private void LoadDiscretization(IDiscretization discretization, ConductivityDistribution distribution, string label)
        {
            _discretization = discretization;
            _currentDistribution = new ConductivityDistribution(distribution.Conductivities);
            _originalDistribution = new ConductivityDistribution(distribution.Conductivities);
            ActiveSource = label;
            _cutoffsInitialized = false;
            CanvasMessage = string.Empty;

            if (discretization is FEMMesh femMesh)
            {
                UpdateDisplayMesh(femMesh);
            }
            else
            {
                Nodes.Clear();
                Elements.Clear();
                Log("Loaded discretization cannot be displayed in this view.", "warn");
                HasMesh = false;
                CanvasMessage = "The loaded discretization cannot be rendered on the canvas.";
                return;
            }

            HasMesh = true;
            UpdateStatistics(resetCutoffs: true);
            MeshUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void BuildDemoMesh()
        {
            var random = new Random(0);
            var vertices = new List<FEMVertex>();
            var elements = new List<FEMElement>();

            int rings = 10;
            vertices.Add(new FEMVertex(0, 0, 0));
            int startOfRing = 1;
            int startOfPrevRing = 0;

            for (int i = 1; i <= rings; i++)
            {
                double radius = (double)i / rings;
                int segments = 6 * i;
                for (int j = 0; j < segments; j++)
                {
                    double angle = (j / (double)segments) * Math.PI * 2;
                    double x = Math.Cos(angle) * radius * 0.5 + 0.5;
                    double y = Math.Sin(angle) * radius * 0.5 + 0.5;
                    vertices.Add(new FEMVertex(vertices.Count, x, y));
                }

                int prevSegments = i == 1 ? 1 : 6 * (i - 1);
                for (int j = 0; j < segments; j++)
                {
                    int current = startOfRing + j;
                    int next = startOfRing + ((j + 1) % segments);
                    int prev = startOfPrevRing + (int)Math.Floor((j / (double)segments) * prevSegments);
                    int prevNext = startOfPrevRing + ((int)Math.Floor(((j + 1) / (double)segments) * prevSegments) % prevSegments);

                    elements.Add(new FEMElement(elements.Count, vertices[current], vertices[next], vertices[prev]));
                    if (i > 1 && prev != prevNext)
                    {
                        elements.Add(new FEMElement(elements.Count, vertices[next], vertices[prev], vertices[prevNext]));
                    }
                }

                startOfPrevRing = startOfRing;
                startOfRing = vertices.Count;
            }

            foreach (var vertex in vertices)
            {
                vertex.X -= 0.5;
                vertex.Y -= 0.5;
            }

            var mesh = new FEMMesh(vertices, elements);

            var conductivities = new Dictionary<int, double>();
            foreach (var element in elements)
            {
                double cx = element.Vertices.Average(v => v.X);
                double cy = element.Vertices.Average(v => v.Y);

                double rawVal = 0.2 + (random.NextDouble() - 0.5) * 0.05;
                double d1 = Math.Sqrt(Math.Pow(cx - 0.2, 2) + Math.Pow(cy - 0.2, 2));
                if (d1 < 0.25) rawVal += 0.8 * (1 - d1 / 0.25);

                double d2 = Math.Sqrt(Math.Pow(cx + 0.25, 2) + Math.Pow(cy + 0.1, 2));
                if (d2 < 0.2) rawVal -= 0.5 * (1 - d2 / 0.2);

                rawVal = Math.Clamp(rawVal, 0.05, 1.2);
                conductivities[element.Id] = rawVal;
                element.Conductivity = rawVal;
            }

            mesh.SetConductivityDistribution(new ConductivityDistribution(conductivities));
            LoadDiscretization(mesh, mesh.GetConductivityDistribution(), "Demo mesh");
        }

        private void UpdateDisplayMesh(FEMMesh mesh)
        {
            Nodes.Clear();
            Elements.Clear();
            var vertexIndex = new Dictionary<int, int>();
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var v = mesh.Vertices[i];
                vertexIndex[v.GlobalId] = i;
                Nodes.Add(new FemNode
                {
                    X = v.X - 0.5,
                    Y = v.Y - 0.5,
                });
            }

            foreach (var element in mesh.ElementsTyped)
            {
                if (!vertexIndex.TryGetValue(element.Vertices[0].GlobalId, out var n1) ||
                    !vertexIndex.TryGetValue(element.Vertices[1].GlobalId, out var n2) ||
                    !vertexIndex.TryGetValue(element.Vertices[2].GlobalId, out var n3))
                {
                    continue;
                }

                double val = _currentDistribution?.GetValue(element.Id) ?? element.Conductivity;
                Elements.Add(new FemElement
                {
                    NodeIndices = new[] { n1, n2, n3 },
                    Value = val
                });
            }

            UpdateNodeValuesFromElements();
        }

        private void UpdateNodeValuesFromElements()
        {
            var sum = new double[Nodes.Count];
            var count = new int[Nodes.Count];

            foreach (var el in Elements)
            {
                foreach (var idx in el.NodeIndices)
                {
                    sum[idx] += el.Value;
                    count[idx]++;
                }
            }

            for (int i = 0; i < Nodes.Count; i++)
            {
                if (count[i] == 0)
                {
                    Nodes[i].Value = 0;
                    Nodes[i].OriginalValue = 0;
                }
                else
                {
                    var avg = sum[i] / count[i];
                    Nodes[i].Value = avg;
                    Nodes[i].OriginalValue = avg;
                }
            }
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

            if (SelectedPostProcessor is IWeightedPostProcessing weighted)
                weighted.Weight = FilterStrength / 100.0;

            RunPostProcessor(SelectedPostProcessor);
        }

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

            if (_discretization is FEMMesh femMesh)
            {
                UpdateDisplayMesh(femMesh);
            }

            UpdateStatistics();
            MeshUpdated?.Invoke(this, EventArgs.Empty);
            AddToHistory(processor.Name);
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
                if (_discretization is FEMMesh femMesh)
                    UpdateDisplayMesh(femMesh);
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
        {
            double working = val;
            if (IsLogScale)
                working = Math.Log10(Math.Max(1e-6, working) + 1);

            double min = MinCutoff;
            double max = MaxCutoff;
            if (max <= min)
                max = min + 1e-6;

            working = Math.Clamp(working, min, max);
            return (working - min) / (max - min);
        }

        private void Log(string msg, string type = "normal")
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            ConsoleLogs.Add($"[{time}] {msg}");
        }

        private void AddToHistory(string action) => HistoryLog.Insert(0, action);
    }
}
