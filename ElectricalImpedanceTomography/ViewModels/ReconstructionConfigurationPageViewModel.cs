/// \file ReconstructionConfigurationPageViewModel.cs
/// \brief ViewModel responsible for orchestrating the reconstruction configuration canvas state.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Utility.Classes.Application;
using Utility.Classes.Configurations.ReconstructionConfiguration;
using Utility.Classes.Configurations.ReconstructionConfiguration.Rules;
using Utility.Classes.ReconstructionParameters;

namespace ElectricalImpedanceTomography.ViewModels
{
    /// <summary>
    /// ViewModel for the Reconstruction Configuration Page.
    /// Manages the graph of processing blocks, connections, and user interactions.
    /// </summary>
    public partial class ReconstructionConfigurationPageViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<ReconstructionConfigurationBlock> blocks = new();

        [ObservableProperty]
        private ObservableCollection<ReconstructionConnection> connections = new();

        [ObservableProperty]
        private ReconstructionConfigurationBlock? selectedBlock;

        [ObservableProperty]
        private ReconstructionConnection? selectedConnection;

        [ObservableProperty]
        private double gridSpacing = DefaultGridSpacing;

        [ObservableProperty]
        private double canvasWidth = 2400;

        [ObservableProperty]
        private double canvasHeight = 1600;

        [ObservableProperty]
        private bool isConnectionMode;

        [ObservableProperty]
        private bool canUseConfiguration;

        public ObservableCollection<string> DebugLines { get; } = new();

        public ObservableCollection<string> ValidationIssues { get; } = new();

        // Connection rules: when empty, any block can connect to any other block.
        private readonly Dictionary<BlockType, HashSet<BlockType>> _connectionRules = new();

        public ObservableCollection<BlockType> BlockTypes { get; } = new(ReconstructionBlockRegistry.BlockTypes);

        public const double MinGridSpacing = 12;
        public const double MaxGridSpacing = 96;
        private const double DefaultGridSpacing = 32;
        private bool _isNormalizingWeights;

        public ReconstructionConfigurationPageViewModel()
        {
            var workspaceBlocks = Workspace.GetReconstructionBlocks();
            if (workspaceBlocks.Any())
            {
                foreach (var block in workspaceBlocks)
                {
                    RegisterBlock(block);
                    Blocks.Add(block);
                }
            }
            else
            {
                AddBlock(BlockType.Initialization, 50, 50);
                AddBlock(BlockType.Model, 300, 50);
            }

            ApplyConfigurationToWorkspace();
            Blocks.CollectionChanged += (_, __) => UpdateDiagnostics();
            Connections.CollectionChanged += OnConnectionsCollectionChanged;

            foreach (var connection in Connections)
            {
                RegisterConnection(connection);
            }

            NormalizeConnectionWeights();
            UpdateDiagnostics();
        }

        /// <summary>
        /// Explicitly allows all block types to connect to one another.
        /// </summary>
        public void AllowAllConnections() => _connectionRules.Clear();

        /// <summary>
        /// Configures which targets a given source block type is allowed to connect to.
        /// An empty set for the source means no restrictions (connect to any type).
        /// </summary>
        public void ConfigureConnectionRule(BlockType source, params BlockType[] allowedTargets)
        {
            _connectionRules[source] = allowedTargets?.Length > 0
                ? allowedTargets.ToHashSet()
                : new HashSet<BlockType>();
        }

        public void AddBlock(BlockType type, double x, double y)
        {
            if (type == BlockType.Model && Blocks.Any(b => b.Type == BlockType.Model))
            {
                TrackIssue("Only one Model block can be added to the configuration.");
                return;
            }

            var newBlock = ReconstructionBlockRegistry.CreateBlock(type, x, y);
            RegisterBlock(newBlock);
            Blocks.Add(newBlock);
            SelectBlock(newBlock);
            ApplyConfigurationToWorkspace();
        }

        [RelayCommand]
        public void IncreaseGridSpacing()
        {
            GridSpacing = Math.Min(MaxGridSpacing, GridSpacing + 4);
        }

        [RelayCommand]
        public void DecreaseGridSpacing()
        {
            GridSpacing = Math.Max(MinGridSpacing, GridSpacing - 4);
        }

        private void RegisterBlock(ReconstructionConfigurationBlock block)
        {
            block.ParametersChanged += _ => ApplyConfigurationToWorkspace();
            block.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ReconstructionConfigurationBlock.X) ||
                    args.PropertyName == nameof(ReconstructionConfigurationBlock.Y))
                {
                    ApplyConfigurationToWorkspace();
                }
            };
        }

        [RelayCommand]
        public void SelectBlock(ReconstructionConfigurationBlock? block)
        {
            ClearSelection();
            if (block != null)
            {
                block.IsSelected = true;
                SelectedBlock = block;
            }
        }

        [RelayCommand]
        public void RotateBlock(ReconstructionConfigurationBlock? block)
        {
            if (block == null)
            {
                return;
            }

            block.Rotation = (block.Rotation + 15) % 360;
            SelectBlock(block);
            NotifyLayoutChanged();
        }

        public void SelectConnection(ReconstructionConnection? connection)
        {
            ClearSelection();
            if (connection != null)
            {
                connection.IsSelected = true;
                SelectedConnection = connection;
            }
        }

        public void ClearSelection()
        {
            foreach (var b in Blocks) b.IsSelected = false;
            foreach (var c in Connections) c.IsSelected = false;
            SelectedBlock = null;
            SelectedConnection = null;
        }

        public void UpdateSelection(Rect selectionRect)
        {
            foreach (var block in Blocks)
            {
                var blockRect = new Rect(block.X, block.Y, block.Width, block.Height);
                block.IsSelected = selectionRect.IntersectsWith(blockRect);
            }

            foreach (var conn in Connections)
            {
                var midX = (conn.Source.X + conn.Source.Width + conn.Target.X) / 2;
                var midY = (conn.Source.Y + conn.Source.Height * 0.4 + conn.Target.Y + conn.Target.Height * 0.4) / 2;
                conn.IsSelected = selectionRect.Contains(midX, midY);
            }

            var selectedBlocks = Blocks.Where(b => b.IsSelected).ToList();
            SelectedBlock = selectedBlocks.Count == 1 ? selectedBlocks[0] : null;
        }

        [RelayCommand]
        public void DeleteSelected()
        {
            var blocksToRemove = Blocks.Where(b => b.IsSelected).ToList();
            var connectionsToRemove = Connections.Where(c => c.IsSelected).ToList();

            foreach (var block in blocksToRemove)
            {
                var attached = Connections.Where(c => c.Source == block || c.Target == block).ToList();
                foreach (var c in attached)
                {
                    if (!connectionsToRemove.Contains(c)) connectionsToRemove.Add(c);
                }
            }

            foreach (var c in connectionsToRemove) Connections.Remove(c);
            foreach (var b in blocksToRemove) Blocks.Remove(b);

            SelectedBlock = null;
            SelectedConnection = null;
            ApplyConfigurationToWorkspace();
        }

        public void AddConnection(ReconstructionConfigurationBlock source, ReconstructionConfigurationBlock target)
        {
            if (source == null || target == null || source == target) return;
            if (!CanConnect(source.Type, target.Type)) return;
            if (!ReconstructionConfigurationRules.IsConnectionAllowed(source.Type, target.Type, out var reason))
            {
                if (!string.IsNullOrWhiteSpace(reason))
                    TrackIssue($"{source.Title} -> {target.Title}: {reason}");
                return;
            }
            if (!ReconstructionConfigurationRules.HasAvailableOutput(source, Connections))
            {
                TrackIssue($"{source.Title} has no remaining output connectors.");
                return;
            }

            if (!ReconstructionConfigurationRules.HasAvailableInput(target, Connections))
            {
                TrackIssue($"{target.Title} cannot accept more inputs.");
                return;
            }

            if (!Connections.Any(c => c.Source == source && c.Target == target))
            {
                Connections.Add(new ReconstructionConnection { Source = source, Target = target, Weight = 1.0 });
                ApplyConfigurationToWorkspace();
            }
        }

        private bool CanConnect(BlockType source, BlockType target)
        {
            if (_connectionRules.Count == 0)
            {
                return true; // default: everything can connect to anything
            }

            if (_connectionRules.TryGetValue(source, out var allowedTargets))
            {
                return allowedTargets.Count == 0 || allowedTargets.Contains(target);
            }

            return true;
        }

        [RelayCommand]
        public void ClearAll()
        {
            Connections.Clear();
            Blocks.Clear();
            SelectedBlock = null;
            SelectedConnection = null;
            ApplyConfigurationToWorkspace();
        }

        public string GetBlockTypeName(BlockType type) => ReconstructionBlockRegistry.GetDefinition(type).Title;

        [RelayCommand]
        public async Task SaveConfiguration()
        {
            try
            {
                var issues = ReconstructionConfigurationRules.Validate(Blocks, Connections);
                ValidationIssues.Clear();
                foreach (var issue in issues)
                {
                    ValidationIssues.Add(issue);
                }

                if (issues.Any())
                {
                    await Shell.Current.DisplayAlert("Cannot Save", string.Join("\n", issues), "OK");
                    return;
                }

                var dto = new ConfigurationDto
                {
                    Blocks = Blocks.Select(b => new BlockDto
                    {
                        Id = b.Id,
                        Type = b.Type,
                        X = b.X,
                        Y = b.Y,
                        FontSize = b.FontSize,
                        Width = b.Width,
                        Height = b.Height,
                        Rotation = b.Rotation,
                        Parameters = b.Parameters.Select(p => new ParameterDto
                        {
                            Key = p.Key,
                            Value = GetParamValue(p)
                        }).ToList()
                    }).ToList(),
                    Connections = Connections.Select(c => new ConnectionDto
                    {
                        SourceId = c.Source.Id,
                        TargetId = c.Target.Id,
                        Weight = c.Weight,
                        ControlOffset1X = c.ControlOffset1X,
                        ControlOffset1Y = c.ControlOffset1Y,
                        ControlOffset2X = c.ControlOffset2X,
                        ControlOffset2Y = c.ControlOffset2Y
                    }).ToList()
                };

                string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

                string fileName = $"config_{DateTime.Now:yyyyMMdd_HHmm}.json";
                string path = Path.Combine(FileSystem.AppDataDirectory, fileName);
                await File.WriteAllTextAsync(path, json);

                await Shell.Current.DisplayAlert("Success", $"Configuration saved to: {path}", "OK");
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlert("Error", $"Save failed: {ex.Message}", "OK");
            }
        }

        [RelayCommand]
        public async Task LoadConfiguration()
        {
            try
            {
                var result = await FilePicker.PickAsync(new PickOptions
                {
                    PickerTitle = "Select Configuration JSON",
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI, new[] { ".json" } },
                        { DevicePlatform.Android, new[] { "application/json" } },
                        { DevicePlatform.iOS, new[] { "public.json" } },
                        { DevicePlatform.MacCatalyst, new[] { "public.json" } }
                    })
                });

                if (result == null) return;

                using var stream = await result.OpenReadAsync();
                var dto = await JsonSerializer.DeserializeAsync<ConfigurationDto>(stream);

                if (dto == null) return;

                Blocks.Clear();
                Connections.Clear();

                var idMap = new Dictionary<string, ReconstructionConfigurationBlock>();

                foreach (var bDto in dto.Blocks)
                {
                    var blk = ReconstructionBlockRegistry.CreateBlock(
                        bDto.Type,
                        bDto.X,
                        bDto.Y,
                        bDto.Id,
                        bDto.FontSize <= 0 ? 13 : bDto.FontSize,
                        bDto.Width <= 0 ? 214 : bDto.Width,
                        bDto.Height <= 0 ? 80 : bDto.Height,
                        bDto.Rotation);

                    foreach (var pDto in bDto.Parameters)
                    {
                        var param = blk.Parameters.FirstOrDefault(p => p.Key == pDto.Key);
                        if (param != null) SetParamValue(param, pDto.Value);
                    }

                    RegisterBlock(blk);
                    Blocks.Add(blk);
                    idMap[bDto.Id] = blk;
                }

                foreach (var cDto in dto.Connections)
                {
                    if (idMap.TryGetValue(cDto.SourceId, out var src) && idMap.TryGetValue(cDto.TargetId, out var tgt))
                    {
                        Connections.Add(new ReconstructionConnection
                        {
                            Source = src,
                            Target = tgt,
                            Weight = cDto.Weight,
                            ControlOffset1X = cDto.ControlOffset1X,
                            ControlOffset1Y = cDto.ControlOffset1Y,
                            ControlOffset2X = cDto.ControlOffset2X,
                            ControlOffset2Y = cDto.ControlOffset2Y
                        });
                    }
                }

                ApplyConfigurationToWorkspace();
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlert("Error", $"Load failed: {ex.Message}", "OK");
            }
        }

        [RelayCommand(CanExecute = nameof(CanUseConfiguration))]
        public void UseConfiguration()
        {
            var configuration = CompleteReconstructionConfigurationBuilder.Create(Blocks, Connections);
            Workspace.SetCompleteReconstructionConfiguration(configuration);
            ApplyConfigurationToWorkspace();
        }

        private void ApplyConfigurationToWorkspace()
        {
            ReconstructionBlockRegistry.ApplyBlocksToWorkspace(Blocks);
            UpdateDiagnostics();
        }

        public bool HasOutputCapacity(ReconstructionConfigurationBlock block) =>
            ReconstructionConfigurationRules.HasAvailableOutput(block, Connections);

        public bool HasInputCapacity(ReconstructionConfigurationBlock block) =>
            ReconstructionConfigurationRules.HasAvailableInput(block, Connections);

        private string GetParamValue(ConfigurationParameter p) => p switch
        {
            TextParameter t => t.Value,
            NumberParameter n => n.Value.ToString(),
            BoolParameter b => b.Value.ToString(),
            ChoiceParameter c => c.SelectedOption,
            _ => ""
        };

        private void SetParamValue(ConfigurationParameter p, string value)
        {
            try
            {
                if (p is TextParameter t) t.Value = value;
                else if (p is NumberParameter n) n.Value = double.Parse(value);
                else if (p is BoolParameter b) b.Value = bool.Parse(value);
                else if (p is ChoiceParameter c) c.SelectedOption = value;
            }
            catch { }
        }

        public void NotifyLayoutChanged() => ApplyConfigurationToWorkspace();

        partial void OnCanUseConfigurationChanged(bool value)
        {
            UseConfigurationCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Keeps connection subscriptions and normalized weights in sync when the graph changes.
        /// </summary>
        private void OnConnectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var old in e.OldItems.OfType<ReconstructionConnection>())
                {
                    UnregisterConnection(old);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var added in e.NewItems.OfType<ReconstructionConnection>())
                {
                    RegisterConnection(added);
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var connection in Connections)
                {
                    UnregisterConnection(connection);
                }

                foreach (var connection in Connections)
                {
                    RegisterConnection(connection);
                }
            }

            NormalizeConnectionWeights();
            UpdateDiagnostics();
        }

        /// <summary>
        /// Hooks property change notifications for a connection to react to weight edits.
        /// </summary>
        private void RegisterConnection(ReconstructionConnection connection)
        {
            connection.PropertyChanged -= OnConnectionPropertyChanged;
            connection.PropertyChanged += OnConnectionPropertyChanged;
        }

        /// <summary>
        /// Removes property change subscriptions when a connection is removed from the canvas.
        /// </summary>
        private void UnregisterConnection(ReconstructionConnection connection)
        {
            connection.PropertyChanged -= OnConnectionPropertyChanged;
        }

        /// <summary>
        /// Rebalances connection weights when a weight edit occurs from the UI.
        /// </summary>
        private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReconstructionConnection.Weight))
            {
                if (_isNormalizingWeights)
                {
                    return;
                }

                if (sender is ReconstructionConnection connection)
                {
                    try
                    {
                        _isNormalizingWeights = true;
                        RebalanceWeightGroup(connection);
                    }
                    finally
                    {
                        _isNormalizingWeights = false;
                    }

                    NormalizeConnectionWeights(false);
                    ApplyConfigurationToWorkspace();
                }
            }
        }

        private void UpdateDiagnostics()
        {
            DebugLines.Clear();
            var blockSummary = string.Join(", ", Blocks
                .GroupBy(b => b.Type)
                .Select(g => $"{g.Key}({g.Count()})"));
            DebugLines.Add(string.IsNullOrWhiteSpace(blockSummary)
                ? "Blocks: none"
                : $"Blocks: {blockSummary}");

            if (Connections.Any())
            {
                foreach (var conn in Connections.Take(6))
                {
                    DebugLines.Add($"Connection: {conn.Source.Title} -> {conn.Target.Title}");
                }
                if (Connections.Count > 6)
                {
                    DebugLines.Add($"(+{Connections.Count - 6} more)");
                }
            }
            else
            {
                DebugLines.Add("Connections: none");
            }

            ValidationIssues.Clear();
            foreach (var issue in ReconstructionConfigurationRules.Validate(Blocks, Connections))
            {
                ValidationIssues.Add(issue);
            }

            CanUseConfiguration = !ValidationIssues.Any();
        }

        private void TrackIssue(string message)
        {
            if (!ValidationIssues.Contains(message))
            {
                ValidationIssues.Add(message);
            }
        }

        /// <summary>
        /// Normalizes solver->error, error->regularizer, optimizer input, and optimizer->model weights so each compatible set sums to one.
        /// Connections that are not meant to be weighted (e.g., measurement->error) are forced back to unity and disabled in the UI.
        /// </summary>
        private void NormalizeConnectionWeights(bool redistributeGroups = true)
        {
            if (_isNormalizingWeights)
            {
                return;
            }

            try
            {
                _isNormalizingWeights = true;

                foreach (var connection in Connections)
                {
                    connection.RequiresWeight = false;
                }

                foreach (var connection in Connections.Where(c => c.Source.Type == BlockType.Measurement && c.Target.Type == BlockType.ErrorMetric))
                {
                    connection.Weight = 1.0;
                }

                foreach (var group in GetWeightedConnectionGroups())
                {
                    foreach (var connection in group)
                    {
                        connection.RequiresWeight = true;
                    }

                    if (redistributeGroups)
                    {
                        NormalizeConnectionGroup(group);
                    }
                }
            }
            finally
            {
                _isNormalizingWeights = false;
            }
        }

        /// <summary>
        /// Equalizes weights inside a group so the sum equals one and every edge shares the remaining influence evenly.
        /// </summary>
        private void NormalizeConnectionGroup(IReadOnlyCollection<ReconstructionConnection> connections)
        {
            if (connections.Count == 0)
            {
                return;
            }

            var evenWeight = Math.Round(1.0 / connections.Count, 4);
            var remaining = 1.0;

            foreach (var connection in connections.Take(connections.Count - 1))
            {
                connection.Weight = evenWeight;
                remaining -= evenWeight;
            }

            var last = connections.Last();
            last.Weight = Math.Max(0, Math.Round(remaining, 4));
        }

        /// <summary>
        /// Adjusts all other weights in the affected group after the user edits one value so the sum remains one.
        /// </summary>
        private void RebalanceWeightGroup(ReconstructionConnection changed)
        {
            var group = FindWeightedGroupForConnection(changed);

            if (group.Count == 0)
            {
                return;
            }

            if (group.Count == 1)
            {
                changed.Weight = 1.0;
                return;
            }

            var others = group.Where(c => c != changed).ToList();
            var remainingWeight = Math.Max(0.0, 1.0 - changed.Weight);

            if (!others.Any())
            {
                changed.Weight = 1.0;
                return;
            }

            var evenShare = Math.Round(remainingWeight / others.Count, 4);
            double allocated = 0.0;

            for (int i = 0; i < others.Count; i++)
            {
                var targetWeight = i == others.Count - 1
                    ? Math.Max(0, Math.Round(remainingWeight - allocated, 4))
                    : evenShare;
                others[i].Weight = targetWeight;
                allocated += targetWeight;
            }

            // Fix rounding drift to guarantee a clean sum of one.
            var sum = changed.Weight + others.Sum(c => c.Weight);
            if (others.Any() && Math.Abs(1.0 - sum) > 0.0001)
            {
                var correction = Math.Round(1.0 - sum, 4);
                others.Last().Weight = Math.Max(0, Math.Round(others.Last().Weight + correction, 4));
            }
        }

        private List<ReconstructionConnection> FindWeightedGroupForConnection(ReconstructionConnection connection)
        {
            if (connection.Source.Type == BlockType.Solver && connection.Target.Type == BlockType.ErrorMetric)
            {
                return Connections
                    .Where(c => c.Source.Type == BlockType.Solver && c.Target == connection.Target)
                    .ToList();
            }

            if (connection.Source.Type == BlockType.Model && connection.Target.Type == BlockType.Regularizer)
            {
                return Connections
                    .Where(c => c.Source == connection.Source && c.Target.Type == BlockType.Regularizer)
                    .ToList();
            }

            if (connection.Target.Type == BlockType.Optimizer &&
                (connection.Source.Type == BlockType.ErrorMetric || connection.Source.Type == BlockType.Regularizer))
            {
                return Connections
                    .Where(c => c.Target == connection.Target &&
                                (c.Source.Type == BlockType.ErrorMetric || c.Source.Type == BlockType.Regularizer))
                    .ToList();
            }

            if (connection.Source.Type == BlockType.Optimizer && connection.Target.Type == BlockType.Model)
            {
                return Connections
                    .Where(c => c.Target == connection.Target && c.Source.Type == BlockType.Optimizer)
                    .ToList();
            }

            return new List<ReconstructionConnection>();
        }

        private IEnumerable<IReadOnlyCollection<ReconstructionConnection>> GetWeightedConnectionGroups()
        {
            var solverToError = Connections
                .Where(c => c.Source.Type == BlockType.Solver && c.Target.Type == BlockType.ErrorMetric)
                .GroupBy(c => c.Target)
                .Select(g => (IReadOnlyCollection<ReconstructionConnection>)g.ToList());

            var modelToRegularizer = Connections
                .Where(c => c.Source.Type == BlockType.Model && c.Target.Type == BlockType.Regularizer)
                .GroupBy(c => c.Source)
                .Select(g => (IReadOnlyCollection<ReconstructionConnection>)g.ToList());

            var optimizerInputs = Connections
                .Where(c => c.Target.Type == BlockType.Optimizer &&
                            (c.Source.Type == BlockType.ErrorMetric || c.Source.Type == BlockType.Regularizer))
                .GroupBy(c => c.Target)
                .Select(g => (IReadOnlyCollection<ReconstructionConnection>)g.ToList());

            var optimizerToModel = Connections
                .Where(c => c.Source.Type == BlockType.Optimizer && c.Target.Type == BlockType.Model)
                .GroupBy(c => c.Target)
                .Select(g => (IReadOnlyCollection<ReconstructionConnection>)g.ToList());

            return solverToError
                .Concat(modelToRegularizer)
                .Concat(optimizerInputs)
                .Concat(optimizerToModel);
        }

        partial void OnGridSpacingChanged(double value)
        {
            // trigger diagnostics so connected UI elements reflect new spacing immediately
            UpdateDiagnostics();
        }

        public class ConfigurationDto
        {
            public List<BlockDto> Blocks { get; set; }
            public List<ConnectionDto> Connections { get; set; }
        }

        public class BlockDto
        {
            public string Id { get; set; }
            public BlockType Type { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double FontSize { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double Rotation { get; set; }
            public List<ParameterDto> Parameters { get; set; }
        }

        public class ParameterDto
        {
            public string Key { get; set; }
            public string Value { get; set; }
        }

        public class ConnectionDto
        {
            public string SourceId { get; set; }
            public string TargetId { get; set; }
            public double Weight { get; set; }
            public double ControlOffset1X { get; set; } = 60;
            public double ControlOffset1Y { get; set; }
            public double ControlOffset2X { get; set; } = -60;
            public double ControlOffset2Y { get; set; }
        }
    }
}
