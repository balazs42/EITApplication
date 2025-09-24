using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceLayer;
using System.Collections.ObjectModel;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

using Workspace = Utility.Classes.Application.Workspace;

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
        private int nx = 15;

        [ObservableProperty]
        private int ny = 15;

        [ObservableProperty]
        private bool inhomogenityEditing;

        [ObservableProperty]
        private double inhomogenityValue = 1.0;

        [ObservableProperty]
        private double electrodeContactImpedance = 0.1;

        [ObservableProperty]
        private double electrodeSize = 0.3;

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

        public event Action? MeshChanged;

        public MeshingPageViewModel(IDAQService dAQService)
        {
            _daqService = dAQService;
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

            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
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

            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());

            MeshChanged?.Invoke();
        }

        public void GenerateMesh()
        {
            if (_currentDiscretization != null)
                PushState();
            _currentDiscretization = SelectedMeshType == DiscretizationType.FEM ? GenerateFEMMesh() : GenerateLBMGrid();

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
                    foreach (var el in fem.ElectrodesTyped)
                        el.Length = ElectrodeSize;
            }
        }

        public void AddNoiseToMesh()
        {
            if (_currentDiscretization == null)
                return;

            PushState();
            MeshFactory.AddGaussianNoise(_currentDiscretization);

            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
        }

        private FEMMesh GenerateFEMMesh()
        {
            if (_customPerimeter != null && _customPerimeter.Count > 2)
            {
                var mesh = MeshFactory.CreatePolygonFEMMesh(_customPerimeter, Layers, ElectrodeCount);
                return mesh;
            }

            return SelectedGeometry switch
            {
                GeometryType.Circular => MeshFactory.CreateCircularFEMMesh(Layers, BoundaryFEMVertexCount, ElectrodeCount),
                GeometryType.Rectangular => MeshFactory.CreateRectangularFEMMesh(Nx, Ny, ElectrodeCount, Layers),
                _ => MeshFactory.CreateCircularFEMMesh(Layers, BoundaryFEMVertexCount, ElectrodeCount)
            };
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

        // Removed old CustomPerimeter parsing workflow

        public void RefreshConductivity()
        {
            if (_currentDiscretization == null)
                return;
            var dict = _currentDiscretization.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            _currentDiscretization.SetConductivityDistribution(new ConductivityDistribution(dict));

            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
        }

        public void RefreshLbmElectrodes()
        {
            if (_currentDiscretization is not LBMGrid mesh) return;
            var electrodes = new List<LBMElectrode>();
            int id = 0;
            foreach (var el in mesh.ElementsTyped)
                if (el.IsElectrode)
                    electrodes.Add(new LBMElectrode(id++, el.Id, 0.0, 0.0, ElectrodeContactImpedance));
            mesh.SetElectrodes(electrodes);

            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
        }

        public void RefreshFemElectrodes()
        {
            if (_currentDiscretization is not FEMMesh mesh) return;
            var verts = mesh.Vertices.Where(v => v.IsElectrode).ToList();
            var electrodes = new List<FEMElectrode>();
            for (int i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                v.ElectrodeId = i;
                var el = new FEMElectrode(i, v.GlobalId, 0.0, ElectrodeContactImpedance, 0.0);
                el.FEMVertexIds.Add(v.GlobalId);
                electrodes.Add(el);
            }
            mesh.SetElectrodes(electrodes);

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

        partial void OnSelectedMeshTypeChanged(DiscretizationType value)
        {
            OnPropertyChanged(nameof(IsFEM));
            OnPropertyChanged(nameof(IsLBM));

            var reconstructionParameters = Workspace.GetReconstructionParameters();

            if (IsFEM) reconstructionParameters.DifferentialEquationSolver = Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FEM;
            else if (IsLBM) reconstructionParameters.DifferentialEquationSolver = Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.LBM;

            AutoGenerateMesh();
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
                lbm.PlaceEquidistantElectrodes(value);
                RefreshLbmElectrodes();
            }
            else if (_currentDiscretization is FEMMesh fem)
            {
                fem.PlaceEquidistantElectrodes(value, ElectrodeContactImpedance, ElectrodeSize);
            }

            _currentDiscretization.Metadata.Parameters["electrodeCount"] = value.ToString();
            Workspace.SetDiscretization(_currentDiscretization);
            Workspace.SetOriginalDiscretization(_currentDiscretization.DeepCopy());
            MeshChanged?.Invoke();
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
