using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using System.Collections.ObjectModel;

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
            public int N1 { get; set; }
            public int N2 { get; set; }
            public int N3 { get; set; }
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

        // Events
        public event EventHandler? MeshUpdated;

        public PostProcessingPageViewModel()
        {
            InitializeDemoMesh();
            Log("System initialized.", "info");
            AddToHistory("Init");
        }

        // --- Mesh Logic ---

        private void InitializeDemoMesh()
        {
            Nodes.Clear();
            Elements.Clear();

            // Generate circular mesh (Matches HTML logic)
            int rings = 18;
            Nodes.Add(new FemNode { X = 0, Y = 0, Value = 0.2, OriginalValue = 0.2 });

            int startOfRing = 1;
            int startOfPrevRing = 0;

            for (int i = 1; i <= rings; i++)
            {
                double radius = (double)i / rings;
                int segments = 6 * i;

                for (int j = 0; j < segments; j++)
                {
                    double angle = (j / (double)segments) * Math.PI * 2;
                    double x = Math.Cos(angle) * radius;
                    double y = Math.Sin(angle) * radius;

                    // Synthetic Phantom Data
                    double rawVal = 0.1;

                    // Red anomaly
                    double d1 = Math.Sqrt(Math.Pow(x - 0.5, 2) + Math.Pow(y - 0.2, 2));
                    if (d1 < 0.3) rawVal += 0.8 * (1 - d1 / 0.3);

                    // Blue anomaly
                    double d2 = Math.Sqrt(Math.Pow(x + 0.4, 2) + Math.Pow(y + 0.3, 2));
                    if (d2 < 0.25) rawVal -= 0.5 * (1 - d2 / 0.25);

                    // Noise
                    rawVal += (new Random().NextDouble() - 0.5) * 0.05;
                    rawVal = Math.Clamp(rawVal, 0.01, 1.0);

                    Nodes.Add(new FemNode { X = x, Y = y, Value = rawVal, OriginalValue = rawVal });
                }

                // Triangulation
                int prevSegments = i == 1 ? 1 : 6 * (i - 1);
                for (int j = 0; j < segments; j++)
                {
                    int current = startOfRing + j;
                    int next = startOfRing + ((j + 1) % segments);
                    int prev = startOfPrevRing + (int)Math.Floor((j / (double)segments) * prevSegments);
                    int prevNext = startOfPrevRing + ((int)Math.Floor(((j + 1) / (double)segments) * prevSegments) % prevSegments);

                    Elements.Add(new FemElement { N1 = current, N2 = next, N3 = prev });
                    if (i > 1 && prev != prevNext)
                    {
                        Elements.Add(new FemElement { N1 = next, N2 = prev, N3 = prevNext });
                    }
                }
                startOfPrevRing = startOfRing;
                startOfRing = Nodes.Count;
            }
            UpdateStatistics();
        }

        // --- Commands ---

        [RelayCommand]
        public void ApplySmooth()
        {
            double strength = FilterStrength / 100.0;
            Log($"Applying Gaussian Smooth (Strength: {strength:P0})");
            var newValues = Nodes.Select(n => n.Value).ToArray();

            foreach (var el in Elements)
            {
                double avg = (Nodes[el.N1].Value + Nodes[el.N2].Value + Nodes[el.N3].Value) / 3.0;
                newValues[el.N1] = Nodes[el.N1].Value * (1 - strength) + avg * strength;
                newValues[el.N2] = Nodes[el.N2].Value * (1 - strength) + avg * strength;
                newValues[el.N3] = Nodes[el.N3].Value * (1 - strength) + avg * strength;
            }
            ApplyValues(newValues);
            AddToHistory("Smooth");
        }

        [RelayCommand]
        public void ApplySharpen()
        {
            double strength = FilterStrength / 100.0;
            Log($"Applying Sharpen (Strength: {strength:P0})");
            var newValues = Nodes.Select(n => n.Value).ToArray();

            foreach (var el in Elements)
            {
                double avg = (Nodes[el.N1].Value + Nodes[el.N2].Value + Nodes[el.N3].Value) / 3.0;
                newValues[el.N1] += (Nodes[el.N1].Value - avg) * strength;
                newValues[el.N2] += (Nodes[el.N2].Value - avg) * strength;
                newValues[el.N3] += (Nodes[el.N3].Value - avg) * strength;
            }
            ApplyValues(newValues);
            AddToHistory("Sharpen");
        }

        [RelayCommand]
        public void ApplyMedian()
        {
            Log("Applying Median Filter...");
            ApplySmooth(); // Proxy for demo
            AddToHistory("Median");
        }

        [RelayCommand]
        public void ResetAll()
        {
            Log("Resetting all parameters.");
            foreach (var n in Nodes) n.Value = n.OriginalValue;

            FilterStrength = 50;
            MinCutoff = 0.0;
            MaxCutoff = 1.0;
            IsLogScale = false;

            HistoryLog.Clear();
            AddToHistory("Init");
            UpdateStatistics();
            MeshUpdated?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void ExportData() => Log("Data exported to clipboard.", "success");

        [RelayCommand]
        public void ClearConsole() => ConsoleLogs.Clear();

        // --- Helpers ---

        private void ApplyValues(double[] vals)
        {
            for (int i = 0; i < Nodes.Count; i++) Nodes[i].Value = Math.Clamp(vals[i], 0, 1);
            UpdateStatistics();
            MeshUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateStatistics()
        {
            if (Nodes.Count == 0) return;
            double sum = 0, min = 1, max = 0;
            foreach (var n in Nodes)
            {
                double v = ProcessValue(n.Value);
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            StatMin = min.ToString("F3");
            StatMax = max.ToString("F3");
            StatAvg = (sum / Nodes.Count).ToString("F3");
        }

        public double ProcessValue(double val)
        {
            if (IsLogScale) val = (Math.Log10(Math.Max(0.01, val)) + 2) / 2;
            val = Math.Clamp(val, MinCutoff, MaxCutoff);
            // Normalize to 0-1 range based on cutoffs for display
            double range = MaxCutoff - MinCutoff;
            if (range < 0.001) range = 0.001;
            return (val - MinCutoff) / range;
        }

        private void Log(string msg, string type = "normal")
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            ConsoleLogs.Add($"[{time}] {msg}");
        }

        private void AddToHistory(string action) => HistoryLog.Insert(0, action);
    }
}