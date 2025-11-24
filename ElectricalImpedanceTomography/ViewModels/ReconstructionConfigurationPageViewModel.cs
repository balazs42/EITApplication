using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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

        public ObservableCollection<string> DebugLines { get; } = new();

        public ObservableCollection<string> ValidationIssues { get; } = new();

        // Connection rules: when empty, any block can connect to any other block.
        private readonly Dictionary<BlockType, HashSet<BlockType>> _connectionRules = new();

        public ObservableCollection<BlockType> BlockTypes { get; } = new(ReconstructionBlockRegistry.BlockTypes);

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
            }

            ApplyConfigurationToWorkspace();
            Blocks.CollectionChanged += (_, __) => UpdateDiagnostics();
            Connections.CollectionChanged += (_, __) => UpdateDiagnostics();
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
            var newBlock = ReconstructionBlockRegistry.CreateBlock(type, x, y);
            RegisterBlock(newBlock);
            Blocks.Add(newBlock);
            SelectBlock(newBlock);
            ApplyConfigurationToWorkspace();
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
                var blockRect = new Rect(block.X, block.Y, 200, 80);
                block.IsSelected = selectionRect.IntersectsWith(blockRect);
            }

            foreach (var conn in Connections)
            {
                var midX = (conn.Source.X + 214 + conn.Target.X) / 2;
                var midY = (conn.Source.Y + 30 + conn.Target.Y + 30) / 2;
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
                Connections.Add(new ReconstructionConnection { Source = source, Target = target });
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
                        Parameters = b.Parameters.Select(p => new ParameterDto
                        {
                            Key = p.Key,
                            Value = GetParamValue(p)
                        }).ToList()
                    }).ToList(),
                    Connections = Connections.Select(c => new ConnectionDto
                    {
                        SourceId = c.Source.Id,
                        TargetId = c.Target.Id
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
                    var blk = ReconstructionBlockRegistry.CreateBlock(bDto.Type, bDto.X, bDto.Y, bDto.Id);

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
                        Connections.Add(new ReconstructionConnection { Source = src, Target = tgt });
                    }
                }

                ApplyConfigurationToWorkspace();
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlert("Error", $"Load failed: {ex.Message}", "OK");
            }
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
        }

        private void TrackIssue(string message)
        {
            if (!ValidationIssues.Contains(message))
            {
                ValidationIssues.Add(message);
            }
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
        }
    }
}
