using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceLayer;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

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
        private static readonly MeshType[] MeshTypeValues = Enum.GetValues<MeshType>();
        public IEnumerable<MeshType> MeshTypes => MeshTypeValues;

        [ObservableProperty]
        private MeshType selectedMeshType = MeshType.FEM;

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

        public ObservableCollection<MeshInfo> AvailableMeshes { get; } = [];
        public ObservableCollection<MeshInfo> FilteredMeshes { get; } = [];

        private IMesh? _currentMesh;
        private readonly Stack<IMesh> _undoStack = new();
        private readonly Stack<IMesh> _redoStack = new();
        private IList<(double x, double y)>? _customPerimeter;

        public event Action? MeshChanged;

        public MeshingPageViewModel(IDAQService dAQService)
        {
            _daqService = dAQService;
        }

        public IMesh? GetCurrentMesh() => _currentMesh;

        public void SetCustomPolygon(IList<(double x, double y)> perimeter)
        {
            _customPerimeter = perimeter;
            SelectedGeometry = GeometryType.Custom;
        }

        public void SaveMesh()
        {
            if (_currentMesh is FEMMesh fem)
                _daqService.SaveFEMMesh(fem, Name);
            else if (_currentMesh is LBMMesh lbm)
                _daqService.SaveLBMMesh(lbm, Name);

            LoadAvailableMeshes();
        }

        public void LoadMesh(string filePath)
        {
            _currentMesh = SelectedMeshType == MeshType.FEM
                ? _daqService.LoadFEMMesh(filePath)
                : _daqService.LoadLBMMesh(filePath);

            Workspace.SetMesh(_currentMesh);
        }

        public void LoadMeshFromWorkspace()
        {
            var mesh = Workspace.GetMesh();
            if (mesh == null)
                return;
            _currentMesh = mesh;
            selectedMeshType = mesh is FEMMesh ? MeshType.FEM : MeshType.LBM;
            OnPropertyChanged(nameof(SelectedMeshType));
            OnPropertyChanged(nameof(IsFEM));
            OnPropertyChanged(nameof(IsLBM));
        }

        public void PushState()
        {
            if (_currentMesh != null)
            {
                _undoStack.Push(_currentMesh.DeepCopy());
                _redoStack.Clear();
            }
        }

        [RelayCommand]
        public void Undo()
        {
            if (_undoStack.Count == 0)
                return;
            if (_currentMesh != null)
                _redoStack.Push(_currentMesh.DeepCopy());
            _currentMesh = _undoStack.Pop();
            Workspace.SetMesh(_currentMesh);
            MeshChanged?.Invoke();
        }

        [RelayCommand]
        public void Redo()
        {
            if (_redoStack.Count == 0)
                return;
            if (_currentMesh != null)
                _undoStack.Push(_currentMesh.DeepCopy());
            _currentMesh = _redoStack.Pop();
            Workspace.SetMesh(_currentMesh);
            MeshChanged?.Invoke();
        }

        public void GenerateMesh()
        {
            if (_currentMesh != null)
                PushState();
            _currentMesh = SelectedMeshType == MeshType.FEM ? GenerateFEMMesh() : GenerateLBMMesh();

            Workspace.SetMesh(_currentMesh);

            if (_currentMesh != null)
            {
                _currentMesh.Metadata.CreatedOn = DateTime.UtcNow;
                _currentMesh.Metadata.ElementCount = _currentMesh.GetElements().Count;
            }
            if (_currentMesh != null)
            {
                foreach (var el in _currentMesh.GetElectrodes())
                    el.ZContact = ElectrodeContactImpedance;
                if (_currentMesh is FEMMesh fem)
                    foreach (var el in fem.ElectrodesTyped)
                        el.Length = ElectrodeSize;
            }
        }

        public void AddNoiseToMesh()
        {
            if (_currentMesh == null)
                return;

            PushState();
            MeshFactory.AddGaussianNoise(_currentMesh);
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

        private LBMMesh GenerateLBMMesh()
        {
            if (_customPerimeter != null && _customPerimeter.Count > 2)
            {
                var mesh = MeshFactory.CreateLBMMeshFromPerimeter(Nx, Ny, _customPerimeter, ElectrodeCount);
                return mesh;
            }
            return SelectedGeometry switch
            {
                GeometryType.Rectangular => MeshFactory.CreateRectangularLBMMesh(Nx, Ny, ElectrodeCount),
                _ => CreateCircularLBMMesh()
            };
        }

        private LBMMesh CreateCircularLBMMesh()
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
            return MeshFactory.CreateLBMMeshFromPerimeter(Nx, Ny, pts, ElectrodeCount);
        }

        // Removed old CustomPerimeter parsing workflow

        public void RefreshConductivity()
        {
            if (_currentMesh == null) 
                return;
            var dict = _currentMesh.GetElements().ToDictionary(e => e.Id, e => e.Conductivity);
            _currentMesh.SetConductivityDistribution(new ConductivityDistribution(dict));
        }

        public void RefreshLbmElectrodes()
        {
            if (_currentMesh is not LBMMesh mesh) return;
            var electrodes = new List<LBMElectrode>();
            int id = 0;
            foreach (var el in mesh.ElementsTyped)
                if (el.IsElectrode)
                    electrodes.Add(new LBMElectrode(id++, el.Id, 0.0, 0.0, ElectrodeContactImpedance));
            mesh.SetElectrodes(electrodes);
        }

        public void RefreshFemElectrodes()
        {
            if (_currentMesh is not FEMMesh mesh) return;
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
        }

        public void Clear()
        {
            _currentMesh = null;
            _customPerimeter = null;
            Workspace.SetMesh(null);
            MeshChanged?.Invoke();
        }

        public bool IsFEM => SelectedMeshType == MeshType.FEM;
        public bool IsLBM => SelectedMeshType == MeshType.LBM;

        partial void OnSelectedMeshTypeChanged(MeshType value)
        {
            OnPropertyChanged(nameof(IsFEM));
            OnPropertyChanged(nameof(IsLBM));

            var reconstructionParameters = Workspace.GetReconstructionParameters();

            if (IsFEM) reconstructionParameters.DifferentialEquationSolver = Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.FiniteElementMethod;
            else if (IsLBM) reconstructionParameters.DifferentialEquationSolver = Utility.Classes.ReconstructionParameters.DifferentialEquationSolver.LatticeBoltzmannMethod;

            AutoGenerateMesh();
        }

        partial void OnSelectedGeometryChanged(GeometryType value)
        {
            if (value == GeometryType.Custom)
            {
                _currentMesh = null;
                _customPerimeter = null;
                Workspace.SetMesh(null);
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
            if (_currentMesh == null)
            {
                AutoGenerateMesh();
                return;
            }

            if (_currentMesh is LBMMesh lbm)
            {
                lbm.PlaceEquidistantElectrodes(value);
                RefreshLbmElectrodes();
            }
            else if (_currentMesh is FEMMesh fem)
            {
                fem.PlaceEquidistantElectrodes(value, ElectrodeContactImpedance, ElectrodeSize);
            }

            _currentMesh.Metadata.Parameters["electrodeCount"] = value.ToString();
            Workspace.SetMesh(_currentMesh);
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
            foreach (var m in _daqService.GetMeshes())
                AvailableMeshes.Add(m);
            ApplyMeshFilter();
        }

        public void InvokeMeshChanged()
        {
            MeshChanged?.Invoke();
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
