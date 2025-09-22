using ElectricalImpedanceTomography.Extensions;
using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Discretizer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace ElectricalImpedanceTomography.Views;

public partial class MeshingPage : ContentPage
{
    private readonly MeshingPageViewModel _viewModel;

    // paints for LBM drawing
    private readonly SKPaint _lbmFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black };
    private readonly SKPaint _lbmWall = new() { Style = SKPaintStyle.Fill, Color = SKColors.White };
    private readonly SKPaint _lbmElectrode = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange };
    private readonly SKPaint _lbmStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };
    private readonly SKPaint _lbmSelected = new() { Style = SKPaintStyle.Fill, Color = SKColors.LimeGreen };

    // stroke for FEM
    private readonly SKPaint _femStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };
    private readonly SKPaint _femFill = new() { Style = SKPaintStyle.Fill };
    private readonly SKPaint _electrodeFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Yellow };
    private readonly SKPaint _pointFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.SkyBlue };

    // caching values for coordinate transforms
    private float _cellW, _cellH;
    private float _scale, _marginX, _marginY, _minX, _minY, _meshWidth, _meshHeight;

    // drawing state for custom FEM meshes
    private readonly List<SKPoint> _outlinePoints = [];
    private readonly HashSet<int> _selectedCells = [];
    private bool _isDrawing;
    private bool _outlineClosed;
    private const float PointEpsilon = 5f;

    // dragging state
    private LBMElement? _draggedLbmElectrode;
    private FEMVertex? _draggedFemVertex;

    public MeshingPage()
    {
        InitializeComponent();
        _viewModel = Utility.Composition.Container.ResolveObject<MeshingPageViewModel>();
        BindingContext = _viewModel;
        _viewModel.MeshChanged += () =>
        {
            _selectedCells.Clear();
            _outlinePoints.Clear();
            _outlineClosed = false;
            MeshCanvas.InvalidateSurface();
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var (startColor, endColor) = GetBackgroundPulseColors();
        this.StartBackgroundPulse(startColor, endColor);
        _viewModel.LoadAvailableMeshes();
        _viewModel.LoadMeshFromWorkspace();
        if (_viewModel.GetCurrentMesh() == null)
            _viewModel.GenerateMesh();
        _viewModel.InvokeMeshChanged();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        this.StopBackgroundPulse();
    }

    private static (Color Start, Color End) GetBackgroundPulseColors()
    {
        var theme = Application.Current?.RequestedTheme ?? AppTheme.Light;
        return theme == AppTheme.Dark
            ? (Color.FromArgb("#102024"), Color.FromArgb("#1C3737"))
            : (Color.FromArgb("#D4EFE7"), Color.FromArgb("#C2E3DA"));
    }

    private SKColor ColorForValue(double val, double max)
    {
        double range = max - 1;
        if (range <= 0)
            return new SKColor(0, 0, 255);
        double t = (val - 1) / range;
        t = Math.Clamp(t, 0.0, 1.0);
        byte r = (byte)(255 * t);
        byte b = (byte)(255 * (1 - t));
        return new SKColor(r, 0, b);
    }

    private static async Task ShrinkViewAsync(VisualElement element)
    {
        await element.ScaleTo(0.9, 40);
        await element.ScaleTo(1, 40);
    }

    private void OnMeshCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColor.Parse("#1C2638"));
        var mesh = _viewModel.GetCurrentMesh();
        if (mesh is null)
        {
            if (_outlinePoints.Count > 0)
                DrawPolygonPreview(canvas);
            return;
        }

        if (mesh is LBMGrid lbm)
            DrawLBMGrid(canvas, e.Info, lbm);
        else if (mesh is FEMMesh fem)
            DrawFEMMesh(canvas, e.Info, fem);
    }

    private void DrawLBMGrid(SKCanvas canvas, SKImageInfo info, LBMGrid grid)
    {
        _cellW = (float)info.Width / grid.Nx;
        _cellH = (float)info.Height / grid.Ny;
        for (int y = 0; y < grid.Ny; y++)
        {
            for (int x = 0; x < grid.Nx; x++)
            {
                var el = grid.GetElementAt(x, y);
                SKPaint fill;
                if (_selectedCells.Contains(el.Id))
                    fill = _lbmSelected;
                else if (el.IsElectrode)
                    fill = _lbmElectrode;
                else if (el.IsWall)
                    fill = _lbmWall;
                else
                    fill = _lbmFill;
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
        double max = Math.Max(1.0, elements.Max(el => el.Conductivity));

        using var path = new SKPath();
        foreach (var el in elements)
        {
            var p1 = ToCanvas(el.Vertices[0]);
            var p2 = ToCanvas(el.Vertices[1]);
            var p3 = ToCanvas(el.Vertices[2]);
            path.Reset();
            path.MoveTo(p1); path.LineTo(p2); path.LineTo(p3); path.Close();
            _femFill.Color = ColorForValue(el.Conductivity, max);
            canvas.DrawPath(path, _femFill);
            canvas.DrawPath(path, _femStroke);
        }

        foreach (var v in mesh.Vertices.Where(v => v.IsElectrode))
            canvas.DrawCircle(ToCanvas(v), 4f, _electrodeFill);
    }

        private async void OnClearClicked(object sender, EventArgs e)
        {
            if (sender is VisualElement v) await ShrinkViewAsync(v);
            _outlinePoints.Clear();
            _selectedCells.Clear();
            _outlineClosed = false;
            _isDrawing = false;
            _viewModel.HoveredElementInfo = string.Empty;
            _viewModel.PushState();
            _viewModel.Clear();
            MeshCanvas.InvalidateSurface();
        }

    private async void OnEditClicked(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement v) await ShrinkViewAsync(v);
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

    private async void OnUndoClicked(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement v) await ShrinkViewAsync(v);
        _viewModel.Undo();
        MeshCanvas.InvalidateSurface();
    }

    private async void OnRedoClicked(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement v) await ShrinkViewAsync(v);
        _viewModel.Redo();
        MeshCanvas.InvalidateSurface();
    }

        private void DrawPolygonPreview(SKCanvas canvas)
        {
            if (_outlinePoints.Count < 1)
                return;

            using var path = new SKPath();
            path.MoveTo(_outlinePoints[0]);
            for (int i = 1; i < _outlinePoints.Count; i++)
                path.LineTo(_outlinePoints[i]);
            if (_outlineClosed)
                path.Close();
            canvas.DrawPath(path, _femStroke);

            foreach (var p in _outlinePoints)
                canvas.DrawCircle(p, 3f, _pointFill);
        }

    private async void OnAddNoiseTapped(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement v) await ShrinkViewAsync(v);
        var mesh = _viewModel.GetCurrentMesh();
        if (mesh == null)
        {
            await DisplayAlert("No mesh", "Generate a mesh before adding noise.", "OK");
            return;
        }

        _viewModel.AddNoiseToMesh();
    }

    private async void OnMeshCanvasTouch(object sender, SKTouchEventArgs e)
    {
        var mesh = _viewModel.GetCurrentMesh();
        if (mesh == null)
        {
            await HandlePolygonDrawingAsync(e);
            return;
        }

        if (mesh is LBMGrid lbm)
        {
            int x = (int)(e.Location.X / _cellW);
            int y = (int)(e.Location.Y / _cellH);
            if (x < 0 || x >= lbm.Nx || y < 0 || y >= lbm.Ny)
            {
                _viewModel.HoveredElementInfo = string.Empty;
                return;
            }

            var el = lbm.GetElementAt(x, y);

            if (!_viewModel.InhomogenityEditing)
            {
                if (e.ActionType == SKTouchAction.Moved || e.ActionType == SKTouchAction.Entered)
                    _viewModel.HoveredElementInfo = $"ID: {el.Id} \u03C3: {el.Conductivity:F2}, Wall: {el.IsWall}, Electrode: {el.IsElectrode}";
                e.Handled = true;
                return;
            }

            if (_draggedLbmElectrode != null)
            {
                if (e.ActionType == SKTouchAction.Moved)
                {
                    if (el != _draggedLbmElectrode && !el.IsWall)
                    {
                        _draggedLbmElectrode.IsElectrode = false;
                        el.IsElectrode = true;
                        _draggedLbmElectrode = el;
                        _viewModel.RefreshLbmElectrodes();
                        MeshCanvas.InvalidateSurface();
                    }
                }
                else if (e.ActionType == SKTouchAction.Released)
                {
                    _draggedLbmElectrode = null;
                }
                e.Handled = true;
                return;
            }

            if (e.MouseButton == SKMouseButton.Left && e.ActionType == SKTouchAction.Pressed && el.IsElectrode)
            {
                _draggedLbmElectrode = el;
                e.Handled = true;
                return;
            }

            if (e.MouseButton == SKMouseButton.Right && e.ActionType == SKTouchAction.Pressed)
            {
                _viewModel.PushState();
                el.Conductivity = _viewModel.InhomogenityValue;
                _viewModel.RefreshConductivity();
            }
            else if (e.MouseButton == SKMouseButton.Left && e.ActionType == SKTouchAction.Pressed)
            {
                _viewModel.PushState();
                el.IsWall = !el.IsWall;
                if (el.IsWall) el.IsElectrode = false;
            }

            if (e.ActionType == SKTouchAction.Moved || e.ActionType == SKTouchAction.Entered)
                _viewModel.HoveredElementInfo = $"ID: {el.Id} \u03C3: {el.Conductivity:F2}, Wall: {el.IsWall}, Electrode: {el.IsElectrode}";

            MeshCanvas.InvalidateSurface();
        }

        else if (mesh is FEMMesh fem)
        {
            if (_draggedFemVertex != null)
            {
                if (e.ActionType == SKTouchAction.Moved)
                {
                    var target = FindNearestBoundaryVertex(e.Location, fem);
                    if (target != null && target != _draggedFemVertex)
                    {
                        _draggedFemVertex.IsElectrode = false;
                        target.IsElectrode = true;
                        _draggedFemVertex = target;
                        _viewModel.RefreshFemElectrodes();
                        MeshCanvas.InvalidateSurface();
                    }
                }
                else if (e.ActionType == SKTouchAction.Released)
                {
                    _draggedFemVertex = null;
                }
                e.Handled = true;
                return;
            }

            if (e.ActionType == SKTouchAction.Pressed && e.MouseButton == SKMouseButton.Left)
            {
                var hit = FindElectrodeAt(e.Location, fem);
                if (hit != null)
                {
                    _draggedFemVertex = hit;
                    e.Handled = true;
                    return;
                }
            }

            var pt = e.Location;
            bool found = false;
            foreach (var el in fem.ElementsTyped)
            {
                var a = ToCanvas(el.Vertices[0]);
                var b = ToCanvas(el.Vertices[1]);
                var c = ToCanvas(el.Vertices[2]);
                if (PointInTriangle(pt, a, b, c))
                {
                    found = true;
                    if (_viewModel.InhomogenityEditing && e.MouseButton == SKMouseButton.Left && e.ActionType == SKTouchAction.Pressed)
                    {
                        _viewModel.PushState();
                        el.Conductivity = _viewModel.InhomogenityValue;
                        _viewModel.RefreshConductivity();
                        MeshCanvas.InvalidateSurface();
                    }
                    else if (e.ActionType == SKTouchAction.Moved || e.ActionType == SKTouchAction.Entered)
                    {
                        _viewModel.HoveredElementInfo = $"ID: {el.Id} \u03C3: {el.Conductivity:F2}";
                    }
                    break;
                }
            }
            if (!found)
                _viewModel.HoveredElementInfo = string.Empty;
        }
        else
        {
            _viewModel.HoveredElementInfo = string.Empty;
        }
        e.Handled = true;
    }

    private void ApplySelectionConductivity()
    {
        if (_viewModel.GetCurrentMesh() is not LBMGrid lbm)
            return;

        _viewModel.PushState();
        foreach (var id in _selectedCells)
        {
            var (sx, sy) = lbm.ToLattice(id);
            lbm.GetElementAt(sx, sy).Conductivity = _viewModel.InhomogenityValue;
        }
    _selectedCells.Clear();
    _viewModel.RefreshConductivity();
    MeshCanvas.InvalidateSurface();
    }

    private async Task HandlePolygonDrawingAsync(SKTouchEventArgs e)
    {
        if (e.MouseButton == SKMouseButton.Left && e.ActionType == SKTouchAction.Pressed)
        {
            if (!_isDrawing)
            {
                _outlinePoints.Clear();
                _outlineClosed = false;
                _isDrawing = true;
                _outlinePoints.Add(e.Location);
            }
            else
            {
                var first = _outlinePoints[0];
                if (_outlinePoints.Count >= 3 && Math.Abs(first.X - e.Location.X) < 10 && Math.Abs(first.Y - e.Location.Y) < 10)
                {
                    _outlineClosed = true;
                    _isDrawing = false;
                    _outlinePoints.Add(first);
                }
                else
                {
                    bool tooClose = _outlinePoints.Any(p =>
                        (p.X - e.Location.X) * (p.X - e.Location.X) +
                        (p.Y - e.Location.Y) * (p.Y - e.Location.Y) <= PointEpsilon * PointEpsilon);

                    if (tooClose)
                    {
                        await DisplayAlert("Point Too Close", "Point is too close to another point.", "OK");
                    }
                    else
                    {
                        _outlinePoints.Add(e.Location);
                    }
                }
            }
        }
        MeshCanvas.InvalidateSurface();
        e.Handled = true;
    }

    private void OnUndoAcceleratorInvoked(object sender, EventArgs e)
    {
        _viewModel.Undo();
        MeshCanvas.InvalidateSurface();
    }

    private void OnRedoAcceleratorInvoked(object sender, EventArgs e)
    {
        _viewModel.Redo();
        MeshCanvas.InvalidateSurface();
    }

    private async void OnSaveAcceleratorInvoked(object sender, EventArgs e)
    {
        var name = await DisplayPromptAsync("Save Mesh", "Name", initialValue: _viewModel.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _viewModel.Name = name;
            _viewModel.SaveMesh();
        }
    }

    private void OnMeshSelected(object sender, TappedEventArgs e)
    {
        if (sender is Border border)
        {
            if(border.BindingContext is DiscretizationInfo info)
            {
                _viewModel.LoadMesh(info.FilePath);
                MeshCanvas.InvalidateSurface();
            }
        }
    }

    private void OnMeshRightTapped(object sender, TappedEventArgs e)
    {
        // TODO: implement deleteion logic (right click shows delete popup)
    }

    private async void OnDeleteMeshClicked(object sender, EventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
            return;

        if (item.CommandParameter is not DiscretizationInfo info)
            return;

        bool confirm = await DisplayAlert("Delete Mesh",
            $"Are you sure you want to delete \"{info.Name}\"?",
            "OK",
            "Cancel");

        if (!confirm)
            return;

        _viewModel.DeleteMesh(info);
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

    private FEMVertex? FindElectrodeAt(SKPoint canvasPoint, FEMMesh mesh)
    {
        foreach (var v in mesh.Vertices.Where(v => v.IsElectrode))
        {
            var cp = ToCanvas(v);
            float dx = cp.X - canvasPoint.X;
            float dy = cp.Y - canvasPoint.Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= 8f)
                return v;
        }
        return null;
    }

    private FEMVertex? FindNearestBoundaryVertex(SKPoint canvasPoint, FEMMesh mesh)
    {
        FEMVertex? nearest = null;
        float best = float.MaxValue;
        foreach (var v in mesh.Vertices.Where(v => v.IsBoundary))
        {
            var cp = ToCanvas(v);
            float dx = cp.X - canvasPoint.X;
            float dy = cp.Y - canvasPoint.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist < best)
            {
                best = dist;
                nearest = v;
            }
        }
        return nearest;
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        if (sender is VisualElement v) await ShrinkViewAsync(v);
        var name = await DisplayPromptAsync("Save Mesh", "Name", initialValue: _viewModel.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _viewModel.Name = name;
            _viewModel.SaveMesh();
        }
    }

    private async void OnLoadClicked(object sender, EventArgs e)
    {
        if (sender is VisualElement v) await ShrinkViewAsync(v);
        var file = await FilePicker.Default.PickAsync();
        if (file != null)
        {
            _viewModel.LoadMesh(file.FullPath);
            MeshCanvas.InvalidateSurface();
        }
    }

    private async void OnGenerateClicked(object sender, EventArgs e)
    {
        if (sender is VisualElement v) await ShrinkViewAsync(v);
        if (_outlinePoints.Count > 0 && !_outlineClosed)
        {
            await DisplayAlert("Perimeter Not Closed", "Close the perimeter before generating the mesh.", "OK");
            return;
        }

        if (_outlineClosed && _outlinePoints.Count > 2)
        {
            var outline = _outlinePoints.ToList();
            if (outline.Count > 1 && outline[0].Equals(outline[outline.Count - 1]))
                outline.RemoveAt(outline.Count - 1);

            if (_viewModel.SelectedMeshType == DiscretizationType.LBM)
            {
                var w = MeshCanvas.CanvasSize.Width;
                var h = MeshCanvas.CanvasSize.Height;
                var perimeter = outline.Select(p => (
                    (double)Math.Round(p.X / w * _viewModel.Nx),
                    (double)Math.Round(p.Y / h * _viewModel.Ny)
                )).ToList();
                _viewModel.SetCustomPolygon(perimeter);
            }
            else
            {
                var perimeter = outline.Select(p => ((double)p.X, (double)p.Y)).ToList();
                _viewModel.SetCustomPolygon(perimeter);
            }
            _outlinePoints.Clear();
            _outlineClosed = false;
        }

        _viewModel.GenerateMesh();
        _viewModel.InvokeMeshChanged();
        MeshCanvas.InvalidateSurface();
    }
}
