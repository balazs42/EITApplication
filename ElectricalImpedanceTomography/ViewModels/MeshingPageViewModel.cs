using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceLayer;
using Utility.Classes;
using Utility.Classes.Factories;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

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
        public IEnumerable<MeshType> MeshTypes => Enum.GetValues<MeshType>();

        [ObservableProperty]
        private MeshType selectedMeshType = MeshType.FEM;

        public IEnumerable<GeometryType> GeometryTypes => Enum.GetValues<GeometryType>();

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

        private IMesh? _currentMesh;

        public MeshingPageViewModel(IDAQService dAQService)
        {
            _daqService = dAQService;
        }

        public IMesh? GetCurrentMesh() => _currentMesh;

        public void SaveMesh()
        {
            if (_currentMesh != null)
                _daqService.SaveMesh(_currentMesh, Name);
        }

        public void LoadMesh(string name, DateTime savedAt)
        {
            _daqService.LoadMesh(name, savedAt);
        }

        public void GenerateMesh()
        {
            _currentMesh = selectedMeshType == MeshType.FEM ? GenerateFEMMesh() : GenerateLBMMesh();
        }

        private FEMMesh GenerateFEMMesh()
        {
            return selectedGeometry switch
            {
                GeometryType.Circular => MeshFactory.CreateCircularFEMMesh(Layers, BoundaryFEMVertexCount, ElectrodeCount),
                GeometryType.Rectangular => MeshFactory.CreateRectangularFEMMesh(Nx, Ny, ElectrodeCount),
                GeometryType.Custom => MeshFactory.CreatePolygonFEMMesh(ParseCustomPerimeter(), ElectrodeCount),
                GeometryType.Thorax => MeshFactory.CreateThoraxFEMMesh(ParseCustomPerimeter(), ElectrodeCount),
                _ => MeshFactory.CreateCircularFEMMesh(Layers, BoundaryFEMVertexCount, ElectrodeCount)
            };
        }

        private LBMMesh GenerateLBMMesh()
        {
            return selectedGeometry switch
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
                    electrodes.Add(new LBMElectrode(id++, el.Id, 0.0, 0.0, 0.0));
            mesh.SetElectrodes(electrodes);
        }

        public bool IsFEM => SelectedMeshType == MeshType.FEM;
        public bool IsLBM => SelectedMeshType == MeshType.LBM;
        public bool IsCustomGeometry => SelectedGeometry == GeometryType.Custom;

        partial void OnSelectedMeshTypeChanged(MeshType value)
        {
            OnPropertyChanged(nameof(IsFEM));
            OnPropertyChanged(nameof(IsLBM));
        }

        partial void OnSelectedGeometryChanged(GeometryType value)
        {
            OnPropertyChanged(nameof(IsCustomGeometry));
        }
    }
}
