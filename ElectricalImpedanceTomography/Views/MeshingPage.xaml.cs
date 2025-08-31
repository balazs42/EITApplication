using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
using Utility.Classes.Meshing;
using System.Collections.Generic;
using System.Linq;

namespace ElectricalImpedanceTomography.Views;

public partial class MeshingPage : ContentPage
{
    private readonly MeshingPageViewModel _viewModel;

    // paints for LBM drawing
    private readonly SKPaint _lbmFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.WhiteSmoke };
    private readonly SKPaint _lbmWall = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black };
    private readonly SKPaint _lbmElectrode = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange };
    private readonly SKPaint _lbmStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };
    private readonly SKPaint _lbmSelected = new() { Style = SKPaintStyle.Fill, Color = SKColors.LimeGreen };

    // stroke for FEM
    private readonly SKPaint _femStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };
    private readonly SKPaint _previewElectrode = new() { Style = SKPaintStyle.Fill, Color = SKColors.Yellow };

    // caching values for coordinate transforms
    private float _cellW, _cellH;
    private float _scale, _marginX, _marginY, _minX, _minY, _meshWidth, _meshHeight;

    // drawing state for custom FEM meshes
    private readonly List<SKPoint> _outlinePoints = new();
    private readonly List<SKPoint> _electrodePoints = new();
    private readonly HashSet<int> _selectedCells = new();
    private bool _isDrawing;
    private bool _outlineClosed;

    public MeshingPage()
    {
        InitializeComponent();
        _viewModel = Utility.Composition.Container.ResolveObject<MeshingPageViewModel>();
        BindingContext = _viewModel;
        _viewModel.MeshChanged += () => { _selectedCells.Clear(); MeshCanvas.InvalidateSurface(); };
    }

    private SKColor ColorForValue(double val, double min, double max)
    {
        double mid = (min + max) * 0.5;
        if (val >= mid)
        {
            float t = (float)((val - mid) / (max - mid));
            t = Math.Clamp(t, 0f, 1f);
            byte r = (byte)(255 * t);
            return new SKColor(r, 0, 0);
        }
        else
        {
            float t = (float)((mid - val) / (mid - min));
            t = Math.Clamp(t, 0f, 1f);
            byte b = (byte)(255 * t);
            return new SKColor(0, 0, b);
        }
    }

    private void OnMeshCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.LightGray);
        var mesh = _viewModel.GetCurrentMesh();
        if (mesh is null)
        {
            if (_viewModel.SelectedMeshType == MeshType.FEM && _outlinePoints.Count > 0)
                DrawFEMPreview(canvas);
            return;
        }

        if (mesh is LBMMesh lbm)
            DrawLBMMesh(canvas, e.Info, lbm);
        else if (mesh is FEMMesh fem)
            DrawFEMMesh(canvas, e.Info, fem);
    }

    private void DrawLBMMesh(SKCanvas canvas, SKImageInfo info, LBMMesh mesh)
    {
        _cellW = (float)info.Width / mesh.Nx;
        _cellH = (float)info.Height / mesh.Ny;
        var elems = mesh.ElementsTyped;
        double min = elems.Min(el => el.Conductivity);
        double max = elems.Max(el => el.Conductivity);
        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                SKPaint fill;
                if (_selectedCells.Contains(el.Id))
                    fill = _lbmSelected;
                else if (el.IsElectrode)
                    fill = _lbmElectrode;
                else if (el.IsWall)
                    fill = _lbmWall;
                else
                    fill = new SKPaint { Style = SKPaintStyle.Fill, Color = ColorForValue(el.Conductivity, min, max) };
                var r = SKRect.Create(x * _cellW, y * _cellH, _cellW, _cellH);
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, _lbmStroke);
            }
        }
    }

    private SKPoint ToCanvas(FEMVertex v)
        => new SKPoint((float)(v.X - _minX) * _scale + _marginX,
                       MeshCanvas.CanvasSize.Height - ((float)(v.Y - _minY) * _scale + _marginY));

    private void DrawFEMMesh(SKCanvas canvas, SKImageInfo info, FEMMesh mesh)
    {
        const float pad = 10f;
        float availW = info.Width - 2 * pad;
        float availH = info.Height - 2 * pad;
        var verts = mesh.Vertices;
        _minX = (float)verts.Min(v => v.X);
        _minY = (float)verts.Min(v => v.Y);
        var maxX = (float)verts.Max(v => v.X);
        var maxY = (float)verts.Max(v => v.Y);
        _meshWidth = maxX - _minX;
        _meshHeight = maxY - _minY;
        _scale = Math.Min(availW / _meshWidth, availH / _meshHeight);
        float usedW = _meshWidth * _scale;
        float usedH = _meshHeight * _scale;
        _marginX = pad + (availW - usedW) / 2f;
        _marginY = pad + (availH - usedH) / 2f;

        var elements = mesh.ElementsTyped;
        double min = elements.Min(el => el.Conductivity);
        double max = elements.Max(el => el.Conductivity);

        foreach (var el in elements)
        {
            var p1 = ToCanvas(el.Vertices[0]);
            var p2 = ToCanvas(el.Vertices[1]);
            var p3 = ToCanvas(el.Vertices[2]);
            using var path = new SKPath();
            path.MoveTo(p1); path.LineTo(p2); path.LineTo(p3); path.Close();
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = ColorForValue(el.Conductivity, min, max) };
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, _femStroke);
        }

        using var electrodeFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Yellow };
        foreach (var v in mesh.Vertices.Where(v => v.IsElectrode))
            canvas.DrawCircle(ToCanvas(v), 4f, electrodeFill);
    }

    private void OnClearClicked(object sender, EventArgs e)
    {
        // TODO: Clear canvas
    }

    private void OnEditClicked(object sender, TappedEventArgs e)
    {
        bool isChecked = EditingCheckbox.IsChecked;

        if (isChecked)
        {
            EditingCheckbox.IsChecked = false;
            _viewModel.InhomogenityEditing = false;
        }
        else
        {
            EditingCheckbox.IsChecked = true;
            _viewModel.InhomogenityEditing = true;
        }

    }

    private void DrawFEMPreview(SKCanvas canvas)
    {
        if (_outlinePoints.Count < 2)
            return;

        using var path = new SKPath();
        path.MoveTo(_outlinePoints[0]);
        for (int i = 1; i < _outlinePoints.Count; i++)
            path.LineTo(_outlinePoints[i]);
        if (_outlineClosed)
            path.Close();
        canvas.DrawPath(path, _femStroke);

        foreach (var p in _electrodePoints)
            canvas.DrawCircle(p, 4f, _previewElectrode);
    }

    private void OnMeshCanvasTouch(object sender, SKTouchEventArgs e)
    {
        var mesh = _viewModel.GetCurrentMesh();
        if (mesh == null)
        {
            if (_viewModel.SelectedMeshType == MeshType.FEM)
                HandleFEMDrawing(e);
            return;
        }

        if (mesh is LBMMesh lbm)
        {
            int x = (int)(e.Location.X / _cellW);
            int y = (int)(e.Location.Y / _cellH);
            if (x < 0 || x >= lbm.Nx || y < 0 || y >= lbm.Ny)
                return;
            var el = lbm.GetElementAt(x, y);

            if (_viewModel.InhomogenityEditing)
            {

                if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
                {
                    el.Conductivity = _viewModel.InhomogenityValue;
                    _viewModel.RefreshConductivity();
                }
                else if (e.MouseButton == SKMouseButton.Left && e.ActionType == SKTouchAction.Pressed)
                {
                    el.IsWall = !el.IsWall;
                    if (el.IsWall) el.IsElectrode = false;
                }
            }
            else
            {

                if (e.MouseButton == SKMouseButton.Left && e.ActionType == SKTouchAction.Pressed)
                {
                    el.IsWall = !el.IsWall;
                    if (el.IsWall) el.IsElectrode = false;
                }
                else if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
                {
                    el.IsElectrode = !el.IsElectrode;
                    if (el.IsElectrode) el.IsWall = false;
                    _viewModel.RefreshLbmElectrodes();
                }
            }
            MeshCanvas.InvalidateSurface();
        }
        else if (mesh is FEMMesh fem)
        {
            var pt = e.Location;
            foreach (var el in fem.ElementsTyped)
            {
                var a = ToCanvas(el.Vertices[0]);
                var b = ToCanvas(el.Vertices[1]);
                var c = ToCanvas(el.Vertices[2]);
                if (PointInTriangle(pt, a, b, c))
                {
                    if (_viewModel.InhomogenityEditing)
                    {
                        el.Conductivity = _viewModel.InhomogenityValue;
                        _viewModel.RefreshConductivity();
                        MeshCanvas.InvalidateSurface();
                    }
                    break;
                }
            }
        }
        e.Handled = true;
    }

    private void ApplySelectionConductivity()
    {
        if (_viewModel.GetCurrentMesh() is not LBMMesh lbm)
            return;

        foreach (var id in _selectedCells)
        {
            var (sx, sy) = lbm.ToLattice(id);
            lbm.GetElementAt(sx, sy).Conductivity = _viewModel.InhomogenityValue;
        }
        _selectedCells.Clear();
        _viewModel.RefreshConductivity();
        MeshCanvas.InvalidateSurface();
    }

    private void HandleFEMDrawing(SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Left)
        {
            if (e.ActionType == SKTouchAction.Pressed)
            {
                _outlinePoints.Clear();
                _electrodePoints.Clear();
                _outlineClosed = false;
                _isDrawing = true;
                _outlinePoints.Add(e.Location);
            }
            else if (e.ActionType == SKTouchAction.Moved && _isDrawing)
            {
                _outlinePoints.Add(e.Location);
            }
            else if (e.ActionType == SKTouchAction.Released && _isDrawing)
            {
                _isDrawing = false;
                _outlinePoints.Add(e.Location);
                if (_outlinePoints.Count > 2)
                {
                    var first = _outlinePoints[0];
                    if (Math.Abs(first.X - e.Location.X) < 5 && Math.Abs(first.Y - e.Location.Y) < 5)
                    {
                        _outlineClosed = true;
                        _outlinePoints[_outlinePoints.Count - 1] = first;
                    }
                }
            }
        }
        else if (e.MouseButton == SKMouseButton.Right && !_isDrawing && _outlinePoints.Count > 2)
        {
            _electrodePoints.Add(e.Location);
        }
        MeshCanvas.InvalidateSurface();
        e.Handled = true;
    }

    private bool PointInTriangle(SKPoint p, SKPoint a, SKPoint b, SKPoint c)
    {
        var v0 = b - a;
        var v1 = c - a;
        var v2 = p - a;
        float dot00 = v0.X * v0.X + v0.Y * v0.Y;
        float dot01 = v0.X * v1.X + v0.Y * v1.Y;
        float dot02 = v0.X * v2.X + v0.Y * v2.Y;
        float dot11 = v1.X * v1.X + v1.Y * v1.Y;
        float dot12 = v1.X * v2.X + v1.Y * v2.Y;
        float invDen = 1 / (dot00 * dot11 - dot01 * dot01);
        float u = (dot11 * dot02 - dot01 * dot12) * invDen;
        float v = (dot00 * dot12 - dot01 * dot02) * invDen;
        return (u >= 0) && (v >= 0) && (u + v <= 1);
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        var name = await DisplayPromptAsync("Save Mesh", "Name", initialValue: _viewModel.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _viewModel.Name = name;
            _viewModel.SaveMesh();
        }
    }

    private async void OnLoadClicked(object sender, EventArgs e)
    {
        var file = await FilePicker.Default.PickAsync();
        if (file != null)
        {
            _viewModel.LoadMesh(file.FullPath);
            MeshCanvas.InvalidateSurface();
        }
    }

    private void OnGenerateClicked(object sender, EventArgs e)
    {
        if (_viewModel.SelectedMeshType == MeshType.FEM && _outlineClosed && _outlinePoints.Count > 2)
        {
            var perimeter = _outlinePoints.Select(p => ((double)p.X, (double)p.Y)).ToList();
            var electrodes = _electrodePoints.Select(p => ((double)p.X, (double)p.Y)).ToList();
            _viewModel.SetCustomPolygon(perimeter, electrodes);
            _outlinePoints.Clear();
            _electrodePoints.Clear();
            _outlineClosed = false;
        }
        _viewModel.GenerateMesh();
        MeshCanvas.InvalidateSurface();
    }
}
