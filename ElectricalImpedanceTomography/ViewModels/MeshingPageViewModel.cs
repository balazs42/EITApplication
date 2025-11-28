using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceLayer;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;

using Workspace = Utility.Classes.Application.Workspace;
using Utility.Classes.Reconstruction.VirtualElectrodes;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class MeshingPageViewModel : BaseViewModel
    {
        private readonly IDAQService _daqService;

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private DateTime saveTime;

        // mesh parameter bindings
        private static readonly DiscretizationType[] MeshTypeValues = Enum.GetValues<DiscretizationType>();
        public IEnumerable<DiscretizationType> MeshTypes => MeshTypeValues;

        [ObservableProperty]
        private DiscretizationType selectedMeshType = DiscretizationType.FEM;

        private static readonly DrivePattern[] DrivePatternValues = Enum.GetValues<DrivePattern>();
        public IEnumerable<DrivePattern> DrivePatterns => DrivePatternValues;

        [ObservableProperty]
        private DrivePattern selectedDrivePattern = DrivePattern.Adjecent;

        public IList<string> MatlabModelTypes { get; } = new List<string> { "c2c2" };

        [ObservableProperty]
        private string selectedMatlabModelType = "c2c2";

        private static readonly GeometryType[] GeometryTypeValues = Enum.GetValues<GeometryType>();
        public IEnumerable<GeometryType> GeometryTypes => GeometryTypeValues;

        [ObservableProperty]
        private GeometryType selectedGeometry = GeometryType.Circular;

        [ObservableProperty]
        private int layers = 2;

        [ObservableProperty]
        private int boundaryFEMVertexCount = 16;

        [ObservableProperty]
        private int electrodeCount = 16;

        [ObservableProperty]
        private int nx = 27;

        [ObservableProperty]
        private int ny = 27;

        [ObservableProperty]
        private bool inhomogenityEditing = true;

        [ObservableProperty]
        private double inhomogenityValue = 1.0;

        [ObservableProperty]
        private double electrodeContactImpedance = 0.1;

        [ObservableProperty]
        private double electrodeSize = 0.3;

        [ObservableProperty]
        private int femElectrodeNodeCount = 1;

        [ObservableProperty]
        private string hoveredElementInfo = string.Empty;

        [ObservableProperty]
        private string meshSearchText = string.Empty;

        public ObservableCollection<DiscretizationInfo> AvailableMeshes { get; } = [];
        public ObservableCollection<DiscretizationInfo> FilteredMeshes { get; } = [];

        private IDiscretization? _currentDiscretization;
        private readonly Stack<IDiscretization> _undoStack = new();
        private readonly Stack<IDiscretization> _redoStack = new();
        private IList<(double x, double y)>? _customPerimeter;

        // Event invoked when the mesh has changed which indicates a UI redraw is needed
        public event Action? MeshChanged;

        public VirtualElectrodeSettings VirtualElectrodeSettings => Workspace.GetReconstructionParameters().VirtualElectrodeSettings;

        public MeshingPageViewModel(IDAQService dAQService)
        {
            _daqService = dAQService;
            VirtualElectrodeSettings.PropertyChanged += (_, _) => ReapplyVirtualElectrodeLayout();
        }

        public IDiscretization? GetCurrentMesh() => _currentDiscretization;

        public void SetCustomPolygon(IList<(double x, double y)> perimeter)
        {
            _customPerimeter = perimeter;
            SelectedGeometry = GeometryType.Custom;
        }

        public void SaveMesh()
        {
            if (_currentDiscretization is FEMMesh fem)
                _daqService.SaveFEMMesh(fem, Name);
            else if (_currentDiscretization is LBMGrid lbm)
                _daqService.SaveLBMGrid(lbm, Name);

            LoadAvailableMeshes();
        }

        public void LoadMesh(string filePath)
        {
            _currentDiscretization = SelectedMeshType == DiscretizationType.FEM
                ? _daqService.LoadFEMMesh(filePath)
                : _daqService.LoadLBMGrid(filePath);

            Name = Path.GetFileNameWithoutExtension(filePath);

            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());

            SelectedMeshType = _currentDiscretization is FEMMesh ? DiscretizationType.FEM : DiscretizationType.LBM;
            OnPropertyChanged(nameof(IsFEM));
            OnPropertyChanged(nameof(IsLBM));
            OnPropertyChanged(nameof(SelectedDrivePattern));
        }

        public void LoadMeshFromWorkspace()
        {
            var discretization = Workspace.GetDiscretization();
            if (discretization == null)
                return;
            _currentDiscretization = discretization;
            SelectedMeshType = discretization is FEMMesh ? DiscretizationType.FEM : DiscretizationType.LBM;
            OnPropertyChanged(nameof(SelectedMeshType));
            OnPropertyChanged(nameof(IsFEM));
            OnPropertyChanged(nameof(IsLBM));
            OnPropertyChanged(nameof(SelectedDrivePattern));
        }

        public void PushState()
        {
            if (_currentDiscretization != null)
            {
                _undoStack.Push(_currentDiscretization.DeepCopy());
                _redoStack.Clear();
            }
        }

        [RelayCommand]
        public void Undo()
        {
            if (_undoStack.Count == 0)
                return;
            if (_currentDiscretization != null)
                _redoStack.Push(_currentDiscretization.DeepCopy());
            _currentDiscretization = _undoStack.Pop();

            // Revert to last state and store it as the original discretization
            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());

            MeshChanged?.Invoke();
        }

        [RelayCommand]
        public void Redo()
        {
            if (_redoStack.Count == 0)
                return;
            if (_currentDiscretization != null)
                _undoStack.Push(_currentDiscretization.DeepCopy());
            _currentDiscretization = _redoStack.Pop();

            // Revert to last state and store it as the original discretization
            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());

            MeshChanged?.Invoke();
        }

        public void GenerateMesh()
        {
            if (_currentDiscretization != null)
                PushState();
            _currentDiscretization = SelectedMeshType == DiscretizationType.FEM ? GenerateFEMMesh() : GenerateLBMGrid();

            // Store it as the current and original discretization
            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());

            if (_currentDiscretization != null)
            {
                _currentDiscretization.Metadata.CreatedOn = DateTime.UtcNow;
                _currentDiscretization.Metadata.ElementCount = _currentDiscretization.GetElements().Count;
            }
            if (_currentDiscretization != null)
            {
                foreach (var el in _currentDiscretization.GetElectrodes())
                    el.ZContact = ElectrodeContactImpedance;
                if (_currentDiscretization is FEMMesh fem)
                    ApplyFemElectrodeLayout(fem);
            }
        }

        public MatlabExportResult? ExportCurrentMeshForMatlab()
        {
            if (_currentDiscretization is not FEMMesh fem)
                return null;

            string exportName = string.IsNullOrWhiteSpace(Name)
                ? (string.IsNullOrWhiteSpace(fem.Metadata?.Generator) ? "mesh" : fem.Metadata.Generator)
                : Name;

            return _daqService.ExportFemMeshForMatlab(fem, exportName, SelectedDrivePattern, SelectedMatlabModelType);
        }

        private FEMMesh GenerateFEMMesh()
        {
            if (_customPerimeter != null && _customPerimeter.Count > 2)
            {
                var mesh = MeshFactory.CreatePolygonFEMMesh(_customPerimeter, Layers, ElectrodeCount, FemElectrodeNodeCount, ElectrodeSize);
                return mesh;
            }

            return SelectedGeometry switch
            {
                GeometryType.Circular => MeshFactory.CreateCircularFEMMesh(Layers, BoundaryFEMVertexCount, ElectrodeCount, FemElectrodeNodeCount, ElectrodeSize),
                GeometryType.Rectangular => MeshFactory.CreateRectangularFEMMesh(Nx, Ny, ElectrodeCount, Layers, FemElectrodeNodeCount, ElectrodeSize),
                _ => MeshFactory.CreateCircularFEMMesh(Layers, BoundaryFEMVertexCount, ElectrodeCount, FemElectrodeNodeCount, ElectrodeSize)
            };
        }

        private void ApplyFemElectrodeLayout(FEMMesh mesh)
        {
            mesh.PlaceEquidistantElectrodes(
                ElectrodeCount,
                ElectrodeContactImpedance,
                ElectrodeSize,
                Math.Max(1, FemElectrodeNodeCount),
                VirtualElectrodeSettings);
        }

        private LBMGrid GenerateLBMGrid()
        {
            if (_customPerimeter != null && _customPerimeter.Count > 2)
            {
                var mesh = MeshFactory.CreateLBMGridFromPerimeter(Nx, Ny, _customPerimeter, ElectrodeCount);
                return mesh;
            }
            return SelectedGeometry switch
            {
                GeometryType.Rectangular => MeshFactory.CreateRectangularLBMGrid(Nx, Ny, ElectrodeCount),
                _ => CreateCircularLBMGrid()
            };
        }

        private LBMGrid CreateCircularLBMGrid()
        {
            int radius = Math.Min(Nx, Ny) / 2 - 1;
            int cx = Nx / 2;
            int cy = Ny / 2;
            var pts = new List<(double x, double y)>();
            const int n = 64;
            for (int i = 0; i < n; i++)
            {
                double th = 2 * Math.PI * i / n;
                pts.Add((cx + radius * Math.Cos(th), cy + radius * Math.Sin(th)));
            }
            return MeshFactory.CreateLBMGridFromPerimeter(Nx, Ny, pts, ElectrodeCount);
        }

        private void ReapplyVirtualElectrodeLayout()
        {
            if (_currentDiscretization is FEMMesh fem)
                fem.ApplyVirtualElectrodes(VirtualElectrodeSettings, ElectrodeContactImpedance);
            else if (_currentDiscretization is LBMGrid lbm)
                lbm.ApplyVirtualElectrodes(VirtualElectrodeSettings);
        
            if (_currentDiscretization != null)
            {
                Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
                Workspace.SetDiscretization(_currentDiscretization);
            }

            MeshChanged?.Invoke();
        }

        
        public void RefreshConductivity()
        {
            if (_currentDiscretization == null)
                return;
            var dict = _currentDiscretization.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            _currentDiscretization.SetConductivityDistribution(new ConductivityDistribution(dict));

            // Refresh workspace references
            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
        }

        public void RefreshLbmElectrodes()
        {
            if (_currentDiscretization is not LBMGrid mesh) return;
            var electrodes = new List<LBMElectrode>();
            int id = 0;
            foreach (var el in mesh.ElementsTyped)
            {
                if (!el.IsElectrode)
                    continue;

                el.ElectrodeId = id;
                electrodes.Add(new LBMElectrode(id++, el.Id, 0.0, 0.0, ElectrodeContactImpedance));
            }
            mesh.SetElectrodes(electrodes);
            if (VirtualElectrodeSettings.UseVirtualElectrodes)
                mesh.ApplyVirtualElectrodes(VirtualElectrodeSettings);

            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
        }

        public void RefreshFemElectrodes()
        {
            if (_currentDiscretization is not FEMMesh mesh) return;

            var templates = mesh.ElectrodesTyped.Cast<FEMElectrode>().ToList();
            var updated = new List<FEMElectrode>(templates.Count);

            foreach (var template in templates)
            {
                var group = mesh.Vertices
                    .Where(v => v.IsElectrode && v.ElectrodeId == template.Id)
                    .ToList();

                if (group.Count == 0)
                {
                    var preserved = new FEMElectrode(template.Id, template.FEMVertexIds ?? new List<int>(), template.Current, template.ZContact, template.Potential, template.IsExcitation, template.IsGround, template.IsMeasuring)
                    {
                        PointElectrode = template.PointElectrode,
                        Length = template.Length
                    };
                    updated.Add(preserved);
                    continue;
                }

                var orderedIds = mesh.OrderVerticesAlongBoundary(group.Select(v => v.GlobalId));
                foreach (var vertex in group)
                    vertex.ElectrodeId = template.Id;

                var electrode = new FEMElectrode(template.Id, orderedIds, template.Current, ElectrodeContactImpedance, template.Potential, template.IsExcitation, template.IsGround, template.IsMeasuring)
                {
                    PointElectrode = orderedIds.Count <= 1,
                    Length = mesh.ComputeElectrodeLength(orderedIds)
                };
                updated.Add(electrode);
            }

            mesh.SetElectrodes(updated);
            if (VirtualElectrodeSettings.UseVirtualElectrodes)
                mesh.ApplyVirtualElectrodes(VirtualElectrodeSettings, ElectrodeContactImpedance);

            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
        }

        public void Clear()
        {
            _currentDiscretization = null;
            _customPerimeter = null;

            Workspace.SetDiscretization(null);
            Workspace.SetOriginalDiscretization(null);

            MeshChanged?.Invoke();
        }

        public bool IsFEM => SelectedMeshType == DiscretizationType.FEM;
        public bool IsLBM => SelectedMeshType == DiscretizationType.LBM;
        public bool ShowLbmConductivityEntry => IsLBM && InhomogenityEditing;

        partial void OnSelectedMeshTypeChanged(DiscretizationType value)
        {
            OnPropertyChanged(nameof(IsFEM));
            OnPropertyChanged(nameof(IsLBM));
            OnPropertyChanged(nameof(ShowLbmConductivityEntry));

            var reconstructionParameters = Workspace.GetReconstructionParameters();

            if (IsFEM) reconstructionParameters.DifferentialEquationSolver = Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FEM;
            else if (IsLBM) reconstructionParameters.DifferentialEquationSolver = Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.LBM;

            AutoGenerateMesh();
        }

        partial void OnInhomogenityEditingChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowLbmConductivityEntry));
        }

        partial void OnSelectedGeometryChanged(GeometryType value)
        {
            if (value == GeometryType.Custom)
            {
                _currentDiscretization = null;
                _customPerimeter = null;

                Workspace.SetDiscretization(null);
                Workspace.SetOriginalDiscretization(null);
                
                MeshChanged?.Invoke();
            }
            else
            {
                _customPerimeter = null;
                AutoGenerateMesh();
            }
        }

        partial void OnNxChanged(int value) => AutoGenerateMesh();

        partial void OnNyChanged(int value) => AutoGenerateMesh();

        partial void OnLayersChanged(int value) => AutoGenerateMesh();

        partial void OnBoundaryFEMVertexCountChanged(int value) => AutoGenerateMesh();
        partial void OnElectrodeCountChanged(int value)
        {
            if (_currentDiscretization == null)
            {
                AutoGenerateMesh();
                return;
            }

            if (_currentDiscretization is LBMGrid lbm)
            {
                lbm.PlaceEquidistantElectrodes(value, VirtualElectrodeSettings);
                RefreshLbmElectrodes();
            }
            else if (_currentDiscretization is FEMMesh fem)
            {
                fem.PlaceEquidistantElectrodes(value, ElectrodeContactImpedance, ElectrodeSize, FemElectrodeNodeCount, VirtualElectrodeSettings);
            }

            _currentDiscretization.Metadata.Parameters["electrodeCount"] = value.ToString();
            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
            MeshChanged?.Invoke();
        }

        partial void OnFemElectrodeNodeCountChanged(int value)
        {
            if (_currentDiscretization is FEMMesh fem)
            {
                ApplyFemElectrodeLayout(fem);
                Workspace.SetDiscretization(_currentDiscretization);
                Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
                MeshChanged?.Invoke();
            }
        }

        partial void OnElectrodeSizeChanged(double value)
        {
            if (_currentDiscretization is FEMMesh fem)
            {
                ApplyFemElectrodeLayout(fem);
                Workspace.SetDiscretization(_currentDiscretization);
                Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
                MeshChanged?.Invoke();
            }
        }

        partial void OnElectrodeContactImpedanceChanged(double value)
        {
            if (_currentDiscretization is FEMMesh fem)
            {
                ApplyFemElectrodeLayout(fem);
                Workspace.SetDiscretization(_currentDiscretization);
                Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
                MeshChanged?.Invoke();
            }
            else if (_currentDiscretization != null)
            {
                foreach (var electrode in _currentDiscretization.GetElectrodes())
                    electrode.ZContact = value;
            }
        }

        partial void OnMeshSearchTextChanged(string value) => ApplyMeshFilter();

        private void AutoGenerateMesh()
        {
            if (SelectedGeometry == GeometryType.Custom && _customPerimeter == null)
                return;
            GenerateMesh();
            MeshChanged?.Invoke();
        }

        public void LoadAvailableMeshes()
        {
            AvailableMeshes.Clear();
            foreach (var m in _daqService.GetDiscretizationInfos())
                AvailableMeshes.Add(m);
            ApplyMeshFilter();
        }

        public void InvokeMeshChanged()
        {
            MeshChanged?.Invoke();
        }

        public void DeleteMesh(DiscretizationInfo mesh)
        {
            if (mesh == null)
                return;

            _daqService.DeleteMesh(mesh.FilePath);
            LoadAvailableMeshes();
        }

        private void ApplyMeshFilter()
        {
            FilteredMeshes.Clear();
            foreach (var m in AvailableMeshes.Where(m =>
                         string.IsNullOrWhiteSpace(MeshSearchText) ||
                         m.Name.Contains(MeshSearchText, StringComparison.OrdinalIgnoreCase)))
                FilteredMeshes.Add(m);
        }
    }
}
