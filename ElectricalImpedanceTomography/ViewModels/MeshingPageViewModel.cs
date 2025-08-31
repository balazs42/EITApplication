using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using System.Linq;
using System.Collections.Generic;

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
        private string customPerimeter = string.Empty;

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

        private IMesh? _currentMesh;
        private IList<(double x, double y)>? _drawnPerimeter;
        private IList<(double x, double y)>? _drawnElectrodes;

        public event Action? MeshChanged;

        public MeshingPageViewModel(IDAQService dAQService)
        {
            _daqService = dAQService;
        }

        public IMesh? GetCurrentMesh() => _currentMesh;

        public void SetCustomPolygon(IList<(double x, double y)> perimeter, IList<(double x, double y)> electrodes)
        {
            _drawnPerimeter = perimeter;
            _drawnElectrodes = electrodes;
            SelectedGeometry = GeometryType.Custom;
            ElectrodeCount = electrodes.Count;
        }

        public void SaveMesh()
        {
            if (_currentMesh is FEMMesh fem)
                _daqService.SaveFEMMesh(fem, Name);
            else if (_currentMesh is LBMMesh lbm)
                _daqService.SaveLBMMesh(lbm, Name);
        }

        public void LoadMesh(string filePath)
        {
            _currentMesh = SelectedMeshType == MeshType.FEM
                ? _daqService.LoadFEMMesh(filePath)
                : _daqService.LoadLBMMesh(filePath);

            Workspace.SetMesh(_currentMesh);
        }

        public void GenerateMesh()
        {
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

            MeshFactory.AddGaussianNoise(_currentMesh);
        }

        private FEMMesh GenerateFEMMesh()
        {
            if (_drawnPerimeter != null && _drawnPerimeter.Count > 2)
            {
                var mesh = MeshFactory.CreatePolygonFEMMesh(_drawnPerimeter, _drawnElectrodes?.Count ?? ElectrodeCount);
                if (_drawnElectrodes != null && _drawnElectrodes.Count > 0)
                    AssignElectrodes(mesh, _drawnElectrodes);
                _drawnPerimeter = null;
                _drawnElectrodes = null;
                return mesh;
            }

            return SelectedGeometry switch
            {
                GeometryType.Circular => MeshFactory.CreateCircularFEMMesh(Layers, BoundaryFEMVertexCount, ElectrodeCount),
                GeometryType.Rectangular => MeshFactory.CreateRectangularFEMMesh(Nx, Ny, ElectrodeCount),
                GeometryType.Custom => MeshFactory.CreatePolygonFEMMesh(ParseCustomPerimeter(), ElectrodeCount),
                GeometryType.Thorax => MeshFactory.CreateThoraxFEMMesh(ParseCustomPerimeter(), ElectrodeCount),
                _ => MeshFactory.CreateCircularFEMMesh(Layers, BoundaryFEMVertexCount, ElectrodeCount)
            };
        }

        private void AssignElectrodes(FEMMesh mesh, IList<(double x, double y)> positions)
        {
            var boundary = mesh.Vertices.Where(v => v.IsBoundary).ToList();
            var electrodes = new List<FEMElectrode>();
            for (int i = 0; i < positions.Count && i < boundary.Count; i++)
            {
                var pos = positions[i];
                int nearest = 0;
                double best = double.MaxValue;
                for (int j = 0; j < boundary.Count; j++)
                {
                    var v = boundary[j];
                    double dx = v.X - pos.x;
                    double dy = v.Y - pos.y;
                    double d = dx * dx + dy * dy;
                    if (d < best)
                    {
                        best = d;
                        nearest = j;
                    }
                }
                var vert = boundary[nearest];
                vert.IsElectrode = true;
                vert.ElectrodeId = i;
                var el = new FEMElectrode(i, vert.GlobalId, 0.0, ElectrodeContactImpedance, 0.0);
                el.FEMVertexIds.Add(vert.GlobalId);
                electrodes.Add(el);
            }
            mesh.SetElectrodes(electrodes);
        }

        private LBMMesh GenerateLBMMesh()
        {
            return SelectedGeometry switch
            {
                GeometryType.Rectangular => MeshFactory.CreateRectangularLBMMesh(Nx, Ny, ElectrodeCount),
                GeometryType.Custom => MeshFactory.CreateLBMMeshFromPerimeter(Nx, Ny, ParseCustomPerimeter(), ElectrodeCount),
                GeometryType.Thorax => MeshFactory.CreateThoraxLBMMesh(Nx, Ny, ParseCustomPerimeter(), ElectrodeCount),
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

        private IList<(double x, double y)> ParseCustomPerimeter()
        {
            var list = new List<(double x, double y)>();
            if (string.IsNullOrWhiteSpace(CustomPerimeter))
                return list;
            var segments = CustomPerimeter.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                var nums = seg.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length >= 2 && double.TryParse(nums[0], out var x) && double.TryParse(nums[1], out var y))
                    list.Add((x, y));
            }
            return list;
        }

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
            Workspace.SetMesh(null);
            MeshChanged?.Invoke();
        }

        public bool IsFEM => SelectedMeshType == MeshType.FEM;
        public bool IsLBM => SelectedMeshType == MeshType.LBM;
        public bool IsCustomGeometry => SelectedGeometry == GeometryType.Custom;

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
            OnPropertyChanged(nameof(IsCustomGeometry));
            AutoGenerateMesh();
        }

        partial void OnNxChanged(int value) => AutoGenerateMesh();

        partial void OnNyChanged(int value) => AutoGenerateMesh();

        private void AutoGenerateMesh()
        {
            if (SelectedMeshType != MeshType.LBM) return;
            GenerateMesh();
            MeshChanged?.Invoke();
        }
    }
}
