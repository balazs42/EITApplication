using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ElectricalImpedanceTomography.Views;

public partial class GradientInspectionPopup : Popup
{
    private readonly ReconstructionPageViewModel _viewModel;
    private readonly List<ReconstructionPageViewModel.GradientHistorySample> _samples = new();
    private readonly List<Point3D> _points = new();
    private readonly List<(int Index, SKPoint Point)> _projectedPoints = new();

    private float _trajectoryRadius = 1f;
    private float _planeY;
    private float _cameraDistance;
    private float _defaultCameraDistance = 5f;
    private float _yaw = 45f;
    private float _pitch = 20f;
    private float _projectionScale = 1f;
    private bool _isDragging;
    private SKPoint? _lastDragPoint;
    private int _selectedIndex = -1;

    private readonly record struct Point3D(float X, float Y, float Z, int Iteration, double Norm);

    public GradientInspectionPopup(ReconstructionPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        LoadData();
        UpdateSelection(_viewModel.SelectedGradientIndex);

        _viewModel.GradientHistoryChanged += OnGradientHistoryChanged;
        _viewModel.GradientSelectionChanged += OnExternalSelectionChanged;
        Closed += OnPopupClosed;
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        Closed -= OnPopupClosed;
        _viewModel.GradientHistoryChanged -= OnGradientHistoryChanged;
        _viewModel.GradientSelectionChanged -= OnExternalSelectionChanged;
    }

    private void OnGradientHistoryChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadData();
            GradientCanvas.InvalidateSurface();
        });
    }

    private void OnExternalSelectionChanged(object? sender, int index)
    {
        MainThread.BeginInvokeOnMainThread(() => UpdateSelection(index));
    }

    private void LoadData()
    {
        var history = _viewModel.GetGradientHistorySnapshot();
        _samples.Clear();
        _samples.AddRange(history);

        RebuildTrajectory();

        if (_samples.Count == 0)
        {
            UpdateSelection(-1);
        }
        else
        {
            int selected = _viewModel.SelectedGradientIndex;
            if (selected < 0 || selected >= _samples.Count)
                selected = _samples.Count - 1;
            UpdateSelection(selected);
        }
    }

    private void RebuildTrajectory()
    {
        _points.Clear();

        if (_samples.Count == 0)
        {
            EmptyStateLabel.IsVisible = true;
            _trajectoryRadius = 1f;
            _planeY = 0f;
            _cameraDistance = Math.Max(_cameraDistance, _defaultCameraDistance);
            UpdateZoomSlider();
            return;
        }

        EmptyStateLabel.IsVisible = false;

        int dimension = _samples[0].Vector.Count;
        if (dimension <= 0)
            dimension = 1;

        var mean = new double[dimension];
        foreach (var sample in _samples)
        {
            int length = Math.Min(sample.Vector.Count, dimension);
            for (int i = 0; i < length; i++)
                mean[i] += sample.Vector[i];
        }
        for (int i = 0; i < dimension; i++)
            mean[i] /= _samples.Count;

        var centered = new List<double[]>(_samples.Count);
        for (int i = 0; i < _samples.Count; i++)
        {
            var vec = _samples[i].GetVectorCopy();
            if (vec.Length < dimension)
                Array.Resize(ref vec, dimension);
            for (int j = 0; j < dimension; j++)
                vec[j] -= mean[j];
            centered.Add(vec);
        }

        var basis = BuildOrthonormalBasis(centered, dimension);

        double maxRadius = 0.0;
        double minY = double.MaxValue;

        for (int i = 0; i < centered.Count; i++)
        {
            var c = centered[i];
            double x = basis.Count > 0 ? Dot(c, basis[0]) : 0.0;
            double y = basis.Count > 1 ? Dot(c, basis[1]) : 0.0;
            double z = basis.Count > 2 ? Dot(c, basis[2]) : 0.0;

            var point = new Point3D((float)x, (float)y, (float)z, _samples[i].Iteration, _samples[i].Norm);
            _points.Add(point);

            double radius = Math.Sqrt(x * x + y * y + z * z);
            if (radius > maxRadius)
                maxRadius = radius;
            if (y < minY)
                minY = y;
        }

        _trajectoryRadius = maxRadius > 1e-6 ? (float)maxRadius : 1f;
        _planeY = (float)(minY - 0.2 * _trajectoryRadius);
        _defaultCameraDistance = Math.Max(_trajectoryRadius * 3f, 4f);
        if (_cameraDistance <= 0f)
            _cameraDistance = _defaultCameraDistance;

        UpdateZoomSlider();
        GradientCanvas.InvalidateSurface();
    }

    private void UpdateSelection(int index)
    {
        if (index < 0 || index >= _samples.Count)
        {
            _selectedIndex = -1;
            SelectionLabel.Text = _samples.Count == 0
                ? "No gradient data yet"
                : "Select a sample to inspect";
            GradientCanvas.InvalidateSurface();
            return;
        }

        if (_selectedIndex == index)
            return;

        _selectedIndex = index;
        var sample = _samples[index];
        SelectionLabel.Text = $"Iteration {sample.Iteration}: ‖∇J‖ = {sample.Norm:F4}";
        GradientCanvas.InvalidateSurface();
    }

    private void UpdateZoomSlider()
    {
        double minDistance = Math.Max(_trajectoryRadius * 0.8f, 1.0f);
        double maxDistance = Math.Max(_trajectoryRadius * 8f, minDistance + 0.5f);

        ZoomSlider.Minimum = minDistance;
        ZoomSlider.Maximum = maxDistance;

        if (_cameraDistance < minDistance || _cameraDistance > maxDistance)
            _cameraDistance = (float)Math.Clamp(_cameraDistance, minDistance, maxDistance);

        ZoomSlider.Value = _cameraDistance;
        ZoomSlider.IsEnabled = _samples.Count > 0;
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel()
    {
        float normalized = _trajectoryRadius > 1e-4f ? _cameraDistance / _trajectoryRadius : _cameraDistance;
        ZoomValueLabel.Text = $"{normalized:0.0}×";
    }

    private void OnZoomSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        _cameraDistance = (float)e.NewValue;
        UpdateZoomLabel();
        GradientCanvas.InvalidateSurface();
    }

    private void OnResetViewClicked(object? sender, EventArgs e)
    {
        _yaw = 45f;
        _pitch = 20f;
        _cameraDistance = _defaultCameraDistance;
        UpdateZoomSlider();
        GradientCanvas.InvalidateSurface();
    }

    private void OnGradientCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;

        canvas.Clear(SKColor.Parse("#0B111C"));

        if (_points.Count == 0)
            return;

        _projectionScale = Math.Min(info.Width, info.Height) * 0.6f;

        var viewport = new SKRect(0, 0, info.Width, info.Height);
        var (cameraPosition, forward, right, up) = GetCameraFrame();

        DrawGroundPlane(canvas, viewport, cameraPosition, forward, right, up);
        DrawAxes(canvas, viewport, cameraPosition, forward, right, up);

        _projectedPoints.Clear();
        using var path = new SKPath();
        bool started = false;

        var linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColor.Parse("#3A9CED"),
            StrokeWidth = 2f,
            IsAntialias = true
        };

        foreach (var point in _points.Select((p, i) => (Index: i, Point: p)))
        {
            var projected = ProjectPoint(point.Point, viewport, cameraPosition, forward, right, up);
            if (projected.HasValue)
            {
                var pt = projected.Value;
                _projectedPoints.Add((point.Index, pt));
                if (!started)
                {
                    path.MoveTo(pt);
                    started = true;
                }
                else
                {
                    path.LineTo(pt);
                }
            }
        }

        if (started)
            canvas.DrawPath(path, linePaint);

        using var pointPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColor.Parse("#E3EDFF"), IsAntialias = true };
        using var selectedPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColor.Parse("#FFD166"), IsAntialias = true };
        using var outlinePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 2f, IsAntialias = true };

        foreach (var (index, pt) in _projectedPoints)
        {
            float radius = index == _selectedIndex ? 6f : 4f;
            canvas.DrawCircle(pt, radius, index == _selectedIndex ? selectedPaint : pointPaint);
            if (index == _selectedIndex)
                canvas.DrawCircle(pt, radius + 2f, outlinePaint);
        }
    }

    private void OnGradientCanvasTouch(object? sender, SKTouchEventArgs e)
    {
        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _isDragging = true;
                _lastDragPoint = e.Location;
                e.Handled = true;
                break;
            case SKTouchAction.Moved:
                if (_isDragging && _lastDragPoint.HasValue)
                {
                    var delta = e.Location - _lastDragPoint.Value;
                    _yaw += delta.X * 0.4f;
                    _pitch = Math.Clamp(_pitch - delta.Y * 0.4f, -80f, 80f);
                    _lastDragPoint = e.Location;
                    GradientCanvas.InvalidateSurface();
                }
                e.Handled = true;
                break;
            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                if (_isDragging && _lastDragPoint.HasValue)
                {
                    var totalDx = e.Location.X - _lastDragPoint.Value.X;
                    var totalDy = e.Location.Y - _lastDragPoint.Value.Y;
                    float dragDistance = MathF.Sqrt(totalDx * totalDx + totalDy * totalDy);
                    if (dragDistance < 12f)
                        TrySelectPoint(e.Location);
                }
                _isDragging = false;
                _lastDragPoint = null;
                e.Handled = true;
                break;
            case SKTouchAction.WheelChanged:
                AdjustZoom(1f - (float)e.WheelDelta * 0.1f);
                e.Handled = true;
                break;
        }
    }

    private void AdjustZoom(float factor)
    {
        if (_samples.Count == 0)
            return;

        float min = (float)ZoomSlider.Minimum;
        float max = (float)ZoomSlider.Maximum;
        float newDistance = Math.Clamp(_cameraDistance * factor, min, max);
        _cameraDistance = newDistance;
        ZoomSlider.Value = newDistance;
        UpdateZoomLabel();
        GradientCanvas.InvalidateSurface();
    }

    private void TrySelectPoint(SKPoint location)
    {
        if (_projectedPoints.Count == 0)
            return;

        float threshold = 18f;
        int bestIndex = -1;
        float bestDistance = float.MaxValue;

        foreach (var (index, point) in _projectedPoints)
        {
            float dx = point.X - location.X;
            float dy = point.Y - location.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                bestIndex = index;
            }
        }

        if (bestIndex >= 0 && bestDistance <= threshold)
            _viewModel.SetSelectedGradientIndex(bestIndex);
    }

    private (Vector3 Position, Vector3 Forward, Vector3 Right, Vector3 Up) GetCameraFrame()
    {
        float yawRad = DegreesToRadians(_yaw);
        float pitchRad = DegreesToRadians(_pitch);
        float distance = Math.Max(_cameraDistance, 0.5f);

        var position = new Vector3(
            distance * MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            distance * MathF.Sin(pitchRad),
            distance * MathF.Cos(pitchRad) * MathF.Cos(yawRad));

        var forward = Vector3.Normalize(-position);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        if (right.LengthSquared() < 1e-6f)
            right = Vector3.UnitX;
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        return (position, forward, right, up);
    }

    private SKPoint? ProjectPoint(Point3D point,
                                  SKRect viewport,
                                  Vector3 cameraPosition,
                                  Vector3 forward,
                                  Vector3 right,
                                  Vector3 up)
    {
        var world = new Vector3(point.X, point.Y, point.Z);
        var diff = world - cameraPosition;

        float viewX = Vector3.Dot(diff, right);
        float viewY = Vector3.Dot(diff, up);
        float viewZ = Vector3.Dot(diff, forward);

        if (viewZ <= 0.05f)
            return null;

        float perspective = _projectionScale / viewZ;
        float screenX = viewport.MidX + viewX * perspective;
        float screenY = viewport.MidY - viewY * perspective;
        return new SKPoint(screenX, screenY);
    }

    private void DrawGroundPlane(SKCanvas canvas,
                                 SKRect viewport,
                                 Vector3 cameraPosition,
                                 Vector3 forward,
                                 Vector3 right,
                                 Vector3 up)
    {
        float size = Math.Max(_trajectoryRadius * 2.5f, 2f);
        int divisions = 6;

        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(24, 32, 48, 120) };
        using var strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(64, 82, 110, 120), StrokeWidth = 1f, IsAntialias = true };

        var corners = new[]
        {
            ProjectPoint(new Point3D(-size, _planeY, -size, 0, 0), viewport, cameraPosition, forward, right, up),
            ProjectPoint(new Point3D(size, _planeY, -size, 0, 0), viewport, cameraPosition, forward, right, up),
            ProjectPoint(new Point3D(size, _planeY, size, 0, 0), viewport, cameraPosition, forward, right, up),
            ProjectPoint(new Point3D(-size, _planeY, size, 0, 0), viewport, cameraPosition, forward, right, up)
        };

        if (corners.All(p => p.HasValue))
        {
            using var planePath = new SKPath();
            planePath.MoveTo(corners[0]!.Value);
            planePath.LineTo(corners[1]!.Value);
            planePath.LineTo(corners[2]!.Value);
            planePath.LineTo(corners[3]!.Value);
            planePath.Close();
            canvas.DrawPath(planePath, fillPaint);
            canvas.DrawPath(planePath, strokePaint);
        }

        for (int i = -divisions; i <= divisions; i++)
        {
            float t = size * i / divisions;
            var startX = ProjectPoint(new Point3D(t, _planeY, -size, 0, 0), viewport, cameraPosition, forward, right, up);
            var endX = ProjectPoint(new Point3D(t, _planeY, size, 0, 0), viewport, cameraPosition, forward, right, up);
            if (startX.HasValue && endX.HasValue)
                canvas.DrawLine(startX.Value, endX.Value, strokePaint);

            var startZ = ProjectPoint(new Point3D(-size, _planeY, t, 0, 0), viewport, cameraPosition, forward, right, up);
            var endZ = ProjectPoint(new Point3D(size, _planeY, t, 0, 0), viewport, cameraPosition, forward, right, up);
            if (startZ.HasValue && endZ.HasValue)
                canvas.DrawLine(startZ.Value, endZ.Value, strokePaint);
        }
    }

    private void DrawAxes(SKCanvas canvas,
                          SKRect viewport,
                          Vector3 cameraPosition,
                          Vector3 forward,
                          Vector3 right,
                          Vector3 up)
    {
        float length = Math.Max(_trajectoryRadius * 1.2f, 1.2f);
        var origin = ProjectPoint(new Point3D(0, 0, 0, 0, 0), viewport, cameraPosition, forward, right, up);
        if (!origin.HasValue)
            return;

        using var axisPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };

        var axes = new (Point3D Point, SKColor Color)[]
        {
            (new Point3D(length, 0, 0, 0, 0), SKColor.Parse("#FF6B6B")),
            (new Point3D(0, length, 0, 0, 0), SKColor.Parse("#6BFF95")),
            (new Point3D(0, 0, length, 0, 0), SKColor.Parse("#4D9CFF"))
        };

        foreach (var (point, color) in axes)
        {
            var projected = ProjectPoint(point, viewport, cameraPosition, forward, right, up);
            if (projected.HasValue)
            {
                axisPaint.Color = color;
                canvas.DrawLine(origin.Value, projected.Value, axisPaint);
            }
        }
    }

    private static List<double[]> BuildOrthonormalBasis(List<double[]> vectors, int dimension)
    {
        var basis = new List<double[]>();

        foreach (var vector in vectors)
        {
            var residual = (double[])vector.Clone();
            foreach (var existing in basis)
                SubtractProjection(residual, existing);

            double norm = Norm(residual);
            if (norm > 1e-9)
            {
                Scale(residual, 1.0 / norm);
                basis.Add(residual);
                if (basis.Count == 3)
                    break;
            }
        }

        for (int axis = 0; basis.Count < 3 && axis < dimension; axis++)
        {
            var candidate = new double[dimension];
            candidate[axis] = 1.0;
            foreach (var existing in basis)
                SubtractProjection(candidate, existing);

            double norm = Norm(candidate);
            if (norm > 1e-9)
            {
                Scale(candidate, 1.0 / norm);
                basis.Add(candidate);
            }
        }

        while (basis.Count < 3)
        {
            var fallback = new double[dimension];
            if (dimension > 0)
                fallback[0] = 1.0;
            basis.Add(fallback);
        }

        return basis;
    }

    private static void SubtractProjection(double[] vector, double[] basis)
    {
        double dot = Dot(vector, basis);
        for (int i = 0; i < vector.Length; i++)
            vector[i] -= dot * basis[i];
    }

    private static double Norm(double[] vector) => Math.Sqrt(Dot(vector, vector));

    private static void Scale(double[] vector, double factor)
    {
        for (int i = 0; i < vector.Length; i++)
            vector[i] *= factor;
    }

    private static double Dot(IReadOnlyList<double> a, double[] b)
    {
        double sum = 0.0;
        int length = Math.Min(a.Count, b.Length);
        for (int i = 0; i < length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0.0;
        int length = Math.Min(a.Length, b.Length);
        for (int i = 0; i < length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private static float DegreesToRadians(float degrees) => (float)(Math.PI / 180.0) * degrees;
}
