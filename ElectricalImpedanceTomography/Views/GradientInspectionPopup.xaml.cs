using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ElectricalImpedanceTomography.Views;

public partial class GradientInspectionPopup : Popup
{
    private readonly ReconstructionPageViewModel _viewModel;
    private readonly List<ReconstructionPageViewModel.GradientHistorySample> _samples = new();
    private readonly List<Point3D> _points = new();
    private readonly List<Point3D> _rawPoints = new();
    private readonly List<(int Index, SKPoint Point)> _projectedPoints = new();
    private readonly List<GradientStep> _steps = new();
    private readonly List<double> _stepAngles = new();

    private float _trajectoryRadius = 1f;
    private float _cameraDistance;
    private float _defaultCameraDistance = 5f;
    private float _yaw = 45f;
    private float _pitch = 20f;
    private float _projectionScale = 1f;
    private bool _isDragging;
    private SKPoint? _lastDragPoint;
    private int _selectedIndex = -1;
    private double _minNorm = double.PositiveInfinity;
    private double _maxNorm = double.NegativeInfinity;
    private double _minAngle = double.PositiveInfinity;
    private double _maxAngle = double.NegativeInfinity;
    private float _autoScale = 1f;
    private float _manualScale = 1f;
    private bool _suppressScaleSliderEvent;
    private ValleySurface? _valleySurface;

    // Added missing backing fields used by drawing helpers
    private float _planeY; // computed ground plane height
    private readonly List<ArrowSegment> _arrowSegments = new();
    private float _maxNormValue;
    private float _maxAngleMagnitude;

    private static readonly SKColor PrimaryPlaneFill = new(64, 128, 255, 60);
    private static readonly SKColor PrimaryPlaneStroke = new(64, 128, 255, 140);
    private static readonly SKColor SecondaryPlaneFill = new(255, 209, 102, 45);
    private static readonly SKColor SecondaryPlaneStroke = new(255, 209, 102, 140);
    private static readonly SKColor AngleColdColor = SKColor.Parse("#3A9CED");
    private static readonly SKColor AngleNeutralColor = SKColor.Parse("#FFD166");
    private static readonly SKColor AngleHotColor = SKColor.Parse("#FF7F6B");

    private readonly record struct Point3D(float X, float Y, float Z, int Iteration, double Norm)
    {
        public Vector3 ToVector() => new(X, Y, Z);
    }

    // Added minimal ArrowSegment type to satisfy DrawArrow signature and usage
    private readonly record struct ArrowSegment(Point3D Start,
                                                Point3D End,
                                                float Norm,
                                                float Angle,
                                                int Index);

    private readonly record struct GradientStep(int StartIndex,
                                                int EndIndex,
                                                Point3D Start,
                                                Point3D End,
                                                double Norm,
                                                double AngleDegrees);

    private sealed class ValleySurface
    {
        public ValleySurface(Vector3[,] grid)
        {
            Grid = grid;
        }

        public Vector3[,] Grid { get; }
        public int Rows => Grid.GetLength(0);
        public int Columns => Grid.GetLength(1);
    }

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
        _rawPoints.Clear();
        _points.Clear();
        _steps.Clear();
        _stepAngles.Clear();
        _valleySurface = null;
        _minNorm = double.PositiveInfinity;
        _maxNorm = double.NegativeInfinity;
        _minAngle = double.PositiveInfinity;
        _maxAngle = double.NegativeInfinity;
        _arrowSegments.Clear();
        _maxNormValue = 0f;
        _maxAngleMagnitude = 0f;

        if (_samples.Count == 0)
        {
            EmptyStateLabel.IsVisible = true;
            _trajectoryRadius = 1f;
            _planeY = 0f;
            _autoScale = 1f;
            _cameraDistance = Math.Max(_cameraDistance, _defaultCameraDistance);
            UpdateZoomSlider();
            UpdateScaleSlider();
            UpdateScaleLabel();
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

        double maxRadiusRaw = 0.0;

        for (int i = 0; i < centered.Count; i++)
        {
            var c = centered[i];
            double x = basis.Count > 0 ? Dot(c, basis[0]) : 0.0;
            double y = basis.Count > 1 ? Dot(c, basis[1]) : 0.0;
            double z = basis.Count > 2 ? Dot(c, basis[2]) : 0.0;

            var rawPoint = new Point3D((float)x, (float)y, (float)z, _samples[i].Iteration, _samples[i].Norm);
            _rawPoints.Add(rawPoint);

            if (rawPoint.Norm < _minNorm)
                _minNorm = rawPoint.Norm;
            if (rawPoint.Norm > _maxNorm)
                _maxNorm = rawPoint.Norm;

            double radius = Math.Sqrt(x * x + y * y + z * z);
            if (radius > maxRadiusRaw)
                maxRadiusRaw = radius;
        }

        for (int i = 1; i < _rawPoints.Count; i++)
        {
            double angle = ComputeGradientAngle(_samples[i - 1].Vector, _samples[i].Vector);
            if (!double.IsFinite(angle))
                angle = 0.0;

            if (angle < _minAngle)
                _minAngle = angle;
            if (angle > _maxAngle)
                _maxAngle = angle;

            _stepAngles.Add(angle);
        }

        if (_minNorm == double.PositiveInfinity || _maxNorm == double.NegativeInfinity)
        {
            _minNorm = 0.0;
            _maxNorm = 1.0;
        }

        if (_minAngle == double.PositiveInfinity)
        {
            _minAngle = 0.0;
            _maxAngle = 0.0;
        }

        _autoScale = ComputeAutoScale((float)maxRadiusRaw);
        ApplyScaleToRawPoints();

        var previous = new Point3D(0f, 0f, 0f, -1, 0.0);
        for (int i = 0; i < _points.Count; i++)
        {
            float norm = (float)_samples[i].Norm;
            float angle = 0f;
            _maxNormValue = Math.Max(_maxNormValue, norm);
            _maxAngleMagnitude = Math.Max(_maxAngleMagnitude, MathF.Abs(angle));
            _arrowSegments.Add(new ArrowSegment(previous, _points[i], norm, angle, i));
            previous = _points[i];
        }

        UpdateZoomSlider();
        UpdateScaleSlider();
        UpdateScaleLabel();
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
            UpdateNavigationButtons();
            UpdateValleySurface();
            GradientCanvas.InvalidateSurface();
            return;
        }

        _selectedIndex = index;
        var sample = _samples[index];
        SelectionLabel.Text = $"Iteration {sample.Iteration}: ‖∇J‖ = {sample.Norm:F4}";
        UpdateNavigationButtons();
        UpdateValleySurface();
        GradientCanvas.InvalidateSurface();
    }

    private void ApplyScaleToRawPoints()
    {
        _points.Clear();
        _steps.Clear();
        _valleySurface = null;

        if (_rawPoints.Count == 0)
        {
            _trajectoryRadius = 1f;
            _planeY = 0f;
            return;
        }

        float totalScale = Math.Clamp(_autoScale * _manualScale, 0.01f, 1000f);

        double maxRadius = 0.0;
        double minY = double.MaxValue;

        for (int i = 0; i < _rawPoints.Count; i++)
        {
            var raw = _rawPoints[i];
            var scaled = new Point3D(raw.X * totalScale,
                                     raw.Y * totalScale,
                                     raw.Z * totalScale,
                                     raw.Iteration,
                                     raw.Norm);
            _points.Add(scaled);

            double radius = Math.Sqrt(scaled.X * scaled.X + scaled.Y * scaled.Y + scaled.Z * scaled.Z);
            if (radius > maxRadius)
                maxRadius = radius;
            if (scaled.Y < minY)
                minY = scaled.Y;
        }

        for (int i = 1; i < _points.Count; i++)
        {
            double angle = i - 1 < _stepAngles.Count ? _stepAngles[i - 1] : 0.0;
            _steps.Add(new GradientStep(i - 1, i, _points[i - 1], _points[i], _samples[i].Norm, angle));
        }

        if (minY == double.MaxValue)
            minY = 0.0;

        _trajectoryRadius = maxRadius > 1e-6 ? (float)maxRadius : 1f;
        _planeY = (float)(minY - 0.2 * _trajectoryRadius);
        _defaultCameraDistance = Math.Max(_trajectoryRadius * 3f, 4f);
        if (_cameraDistance <= 0f)
            _cameraDistance = _defaultCameraDistance;

        UpdateValleySurface();
    }

    private void UpdateNavigationButtons()
    {
        if (PrevStepButton == null || NextStepButton == null)
            return;

        bool hasSamples = _samples.Count > 0;
        int visibleCount = GetVisibleSampleCount();

        bool canStepBack = hasSamples && visibleCount > 1;
        bool canStepForward = hasSamples && (_selectedIndex < _samples.Count - 1) && _selectedIndex >= 0;

        if (_selectedIndex < 0 && hasSamples)
            canStepForward = true;

        SetNavigationButtonState(PrevStepButton, canStepBack);
        SetNavigationButtonState(NextStepButton, canStepForward);
    }

    private static void SetNavigationButtonState(Image button, bool enabled)
    {
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1.0 : 0.35;
    }

    private int GetVisibleSampleCount()
    {
        if (_points.Count == 0)
            return 0;

        if (_selectedIndex < 0 || _selectedIndex >= _points.Count)
            return _points.Count;

        return _selectedIndex + 1;
    }

    private void UpdateScaleSlider()
    {
        if (ScaleSlider == null)
            return;

        double minimum = 0.1;
        double maximum = 20.0;

        _suppressScaleSliderEvent = true;
        ScaleSlider.Minimum = minimum;
        ScaleSlider.Maximum = maximum;
        _manualScale = (float)Math.Clamp(_manualScale, minimum, maximum);
        ScaleSlider.Value = _manualScale;
        ScaleSlider.IsEnabled = _samples.Count > 0;
        _suppressScaleSliderEvent = false;
    }

    private void UpdateScaleLabel()
    {
        if (ScaleValueLabel == null)
            return;

        float total = Math.Clamp(_autoScale * _manualScale, 0.01f, 1000f);
        ScaleValueLabel.Text = _samples.Count == 0
            ? "1.0× (auto 1.0×)"
            : $"{total:0.##}× (auto {_autoScale:0.##}×)";
    }

    private void OnScaleSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_suppressScaleSliderEvent)
            return;

        _manualScale = (float)e.NewValue;
        ApplyScaleToRawPoints();
        UpdateScaleLabel();
        GradientCanvas.InvalidateSurface();
    }

    private void UpdateValleySurface()
    {
        _valleySurface = null;

        int visibleCount = GetVisibleSampleCount();
        if (visibleCount < 4)
            return;

        var visiblePoints = _points.Take(visibleCount).ToList();
        var axes = ComputePrincipalAxes(visiblePoints);
        if (axes == null)
            return;

        var axisA = axes[0];
        var axisB = axes[1];
        var normal = axes[2];

        if (axisA.LengthSquared() < 1e-9f || axisB.LengthSquared() < 1e-9f || normal.LengthSquared() < 1e-9f)
            return;

        axisA = Vector3.Normalize(axisA);
        axisB = Vector3.Normalize(axisB);
        normal = Vector3.Normalize(normal);

        var centroid = ComputeCentroid(visiblePoints);

        float extentA = 0f;
        float extentB = 0f;
        float extentNormal = 0f;

        foreach (var point in visiblePoints)
        {
            var diff = point.ToVector() - centroid;
            extentA = Math.Max(extentA, Math.Abs(Vector3.Dot(diff, axisA)));
            extentB = Math.Max(extentB, Math.Abs(Vector3.Dot(diff, axisB)));
            extentNormal = Math.Max(extentNormal, Math.Abs(Vector3.Dot(diff, normal)));
        }

        extentA = Math.Max(extentA * 1.35f, _trajectoryRadius * 0.3f);
        extentB = Math.Max(extentB * 1.35f, _trajectoryRadius * 0.3f);
        extentNormal = Math.Max(extentNormal * 1.2f, _trajectoryRadius * 0.15f);

        if (!TryFitQuadraticSurface(visiblePoints, centroid, axisA, axisB, normal, out var coefficients))
        {
            _valleySurface = BuildPlanarValley(centroid, axisA, axisB, extentA, extentB);
            return;
        }

        _valleySurface = BuildQuadraticValley(coefficients, centroid, axisA, axisB, normal, extentA, extentB, extentNormal);
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

    private void OnPreviousGradientTapped(object? sender, TappedEventArgs e)
    {
        if (_samples.Count == 0)
            return;

        int target = _selectedIndex <= 0 ? 0 : _selectedIndex - 1;
        if (_selectedIndex != target)
            _viewModel.SetSelectedGradientIndex(target);
    }

    private void OnNextGradientTapped(object? sender, TappedEventArgs e)
    {
        if (_samples.Count == 0)
            return;

        int target = _selectedIndex < 0 ? 0 : Math.Min(_selectedIndex + 1, _samples.Count - 1);
        if (_selectedIndex != target)
            _viewModel.SetSelectedGradientIndex(target);
    }

    private void OnGradientCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;

        canvas.Clear(SKColor.Parse("#0B111C"));

        int visibleCount = GetVisibleSampleCount();
        if (_points.Count == 0 || visibleCount == 0)
            return;

        _projectionScale = Math.Min(info.Width, info.Height) * 0.6f;

        var viewport = new SKRect(0, 0, info.Width, info.Height);
        var (cameraPosition, forward, right, up) = GetCameraFrame();

        // Pass required parameters
        DrawGroundPlane(canvas, viewport, cameraPosition, forward, right, up, _planeY, _trajectoryRadius);
        DrawAxes(canvas, viewport, cameraPosition, forward, right, up, _trajectoryRadius);
        DrawErrorValley(canvas, viewport, cameraPosition, forward, right, up);

        _projectedPoints.Clear();
        var projectionMap = new Dictionary<int, SKPoint>(visibleCount);

        for (int i = 0; i < visibleCount; i++)
        {
            var projected = ProjectPoint(_points[i], viewport, cameraPosition, forward, right, up);
            if (projected.HasValue)
            {
                var pt = projected.Value;
                _projectedPoints.Add((i, pt));
                projectionMap[i] = pt;
            }
        }

        DrawGradientSteps(canvas, projectionMap);

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

    private void DrawArrow(SKCanvas canvas, SKPoint start, SKPoint end, ArrowSegment segment)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1e-3f)
            return;

        float normFactor = _maxNormValue > 1e-6f ? segment.Norm / _maxNormValue : 0f;
        normFactor = Math.Clamp(normFactor, 0f, 1f);
        float strokeWidth = 2f + 3f * normFactor;

        float angleFactor = _maxAngleMagnitude > 1e-6f ? MathF.Min(MathF.Abs(segment.Angle) / _maxAngleMagnitude, 1f) : 0f;
        float hue = 40f + 200f * (1f - angleFactor);
        var color = SKColor.FromHsv(hue, 70f, 95f);

        using var linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = color,
            StrokeWidth = strokeWidth,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };

        float arrowLength = MathF.Min(16f + 18f * normFactor, length * 0.45f);
        float unitX = dx / length;
        float unitY = dy / length;

        var arrowBase = new SKPoint(end.X - unitX * arrowLength, end.Y - unitY * arrowLength);
        canvas.DrawLine(start, arrowBase, linePaint);

        float headWidth = arrowLength * 0.6f;
        float perpX = -unitY;
        float perpY = unitX;

        var left = new SKPoint(arrowBase.X + perpX * headWidth * 0.5f,
                               arrowBase.Y + perpY * headWidth * 0.5f);
        var right = new SKPoint(arrowBase.X - perpX * headWidth * 0.5f,
                                arrowBase.Y - perpY * headWidth * 0.5f);

        using var headPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = color.WithAlpha(220),
            IsAntialias = true
        };

        using var headPath = new SKPath();
        headPath.MoveTo(end);
        headPath.LineTo(left);
        headPath.LineTo(right);
        headPath.Close();
        canvas.DrawPath(headPath, headPaint);
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

    private void OnPreviousClicked(object? sender, EventArgs e)
    {
        if (_samples.Count == 0)
            return;

        int index = _selectedIndex;
        if (index < 0)
            index = _samples.Count - 1;

        if (index <= 0)
            return;

        _viewModel.SetSelectedGradientIndex(index - 1);
    }

    private void OnNextClicked(object? sender, EventArgs e)
    {
        if (_samples.Count == 0)
            return;

        int index = _selectedIndex;
        if (index < 0)
            index = _samples.Count - 1;

        if (index >= _samples.Count - 1)
            return;

        _viewModel.SetSelectedGradientIndex(index + 1);
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

    private void DrawGradientSteps(SKCanvas canvas, IReadOnlyDictionary<int, SKPoint> projections)
    {
        if (_steps.Count == 0)
            return;

        int visibleLastIndex = GetVisibleSampleCount() - 1;
        if (visibleLastIndex <= 0)
            return;

        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };

        using var headPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        foreach (var step in _steps)
        {
            if (step.EndIndex > visibleLastIndex)
                break;

            if (!projections.TryGetValue(step.StartIndex, out var start)
                || !projections.TryGetValue(step.EndIndex, out var end))
            {
                continue;
            }

            var direction = new SKPoint(end.X - start.X, end.Y - start.Y);
            float length = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            if (length < 2f)
                continue;

            var color = ColorForAngle(step.AngleDegrees);
            float thickness = ThicknessForNorm(step.Norm);
            float headLength = MathF.Max(thickness * 4f, 8f);
            float headWidth = MathF.Max(thickness * 3f, headLength * 0.55f);

            var unit = new SKPoint(direction.X / length, direction.Y / length);
            var headBase = new SKPoint(end.X - unit.X * headLength, end.Y - unit.Y * headLength);
            var perpendicular = new SKPoint(-unit.Y, unit.X);

            strokePaint.Color = color;
            strokePaint.StrokeWidth = thickness;
            canvas.DrawLine(start, headBase, strokePaint);

            var left = new SKPoint(headBase.X + perpendicular.X * headWidth * 0.5f,
                                   headBase.Y + perpendicular.Y * headWidth * 0.5f);
            var right = new SKPoint(headBase.X - perpendicular.X * headWidth * 0.5f,
                                    headBase.Y - perpendicular.Y * headWidth * 0.5f);

            using var headPath = new SKPath();
            headPath.MoveTo(end);
            headPath.LineTo(left);
            headPath.LineTo(right);
            headPath.Close();

            headPaint.Color = color.WithAlpha(210);
            canvas.DrawPath(headPath, headPaint);
        }
    }

    private void DrawErrorValley(SKCanvas canvas,
                                 SKRect viewport,
                                 Vector3 cameraPosition,
                                 Vector3 forward,
                                 Vector3 right,
                                 Vector3 up)
    {
        if (_valleySurface == null)
            return;

        var surface = _valleySurface;
        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = PrimaryPlaneFill, IsAntialias = true };
        using var strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = PrimaryPlaneStroke, StrokeWidth = 1.3f, IsAntialias = true };
        using var contourPaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SecondaryPlaneStroke.WithAlpha(160), StrokeWidth = 1f, IsAntialias = true };

        for (int row = 0; row < surface.Rows - 1; row++)
        {
            for (int col = 0; col < surface.Columns - 1; col++)
            {
                var p00 = surface.Grid[row, col];
                var p10 = surface.Grid[row + 1, col];
                var p11 = surface.Grid[row + 1, col + 1];
                var p01 = surface.Grid[row, col + 1];

                var s00 = ProjectPoint(new Point3D(p00.X, p00.Y, p00.Z, 0, 0), viewport, cameraPosition, forward, right, up);
                var s10 = ProjectPoint(new Point3D(p10.X, p10.Y, p10.Z, 0, 0), viewport, cameraPosition, forward, right, up);
                var s11 = ProjectPoint(new Point3D(p11.X, p11.Y, p11.Z, 0, 0), viewport, cameraPosition, forward, right, up);
                var s01 = ProjectPoint(new Point3D(p01.X, p01.Y, p01.Z, 0, 0), viewport, cameraPosition, forward, right, up);

                if (!s00.HasValue || !s10.HasValue || !s11.HasValue || !s01.HasValue)
                    continue;

                using var patch = new SKPath();
                patch.MoveTo(s00.Value);
                patch.LineTo(s10.Value);
                patch.LineTo(s11.Value);
                patch.LineTo(s01.Value);
                patch.Close();

                canvas.DrawPath(patch, fillPaint);
                canvas.DrawPath(patch, strokePaint);
            }
        }

        int rowStep = Math.Max(1, surface.Rows / 6);
        for (int row = 0; row < surface.Rows; row += rowStep)
            DrawSurfacePolyline(canvas, surface, true, row, viewport, cameraPosition, forward, right, up, contourPaint);

        int columnStep = Math.Max(1, surface.Columns / 6);
        for (int col = 0; col < surface.Columns; col += columnStep)
            DrawSurfacePolyline(canvas, surface, false, col, viewport, cameraPosition, forward, right, up, contourPaint);
    }

    private void DrawSurfacePolyline(SKCanvas canvas,
                                     ValleySurface surface,
                                     bool alongRows,
                                     int fixedIndex,
                                     SKRect viewport,
                                     Vector3 cameraPosition,
                                     Vector3 forward,
                                     Vector3 right,
                                     Vector3 up,
                                     SKPaint paint)
    {
        using var path = new SKPath();
        bool hasSegment = false;
        int length = alongRows ? surface.Columns : surface.Rows;

        for (int step = 0; step < length; step++)
        {
            var point = alongRows ? surface.Grid[fixedIndex, step] : surface.Grid[step, fixedIndex];
            var projected = ProjectPoint(new Point3D(point.X, point.Y, point.Z, 0, 0), viewport, cameraPosition, forward, right, up);
            if (!projected.HasValue)
            {
                if (hasSegment)
                {
                    canvas.DrawPath(path, paint);
                    path.Reset();
                    hasSegment = false;
                }
                continue;
            }

            if (!hasSegment)
            {
                path.MoveTo(projected.Value);
                hasSegment = true;
            }
            else
            {
                path.LineTo(projected.Value);
            }
        }

        if (hasSegment)
            canvas.DrawPath(path, paint);
    }

    private ValleySurface BuildQuadraticValley(double[] coefficients,
                                               Vector3 centroid,
                                               Vector3 axisA,
                                               Vector3 axisB,
                                               Vector3 normal,
                                               float extentA,
                                               float extentB,
                                               float extentNormal)
    {
        const int resolution = 28;
        var grid = new Vector3[resolution, resolution];

        for (int row = 0; row < resolution; row++)
        {
            float tRow = resolution == 1 ? 0f : row / (float)(resolution - 1);
            float u = -extentA + (2f * extentA) * tRow;

            for (int col = 0; col < resolution; col++)
            {
                float tCol = resolution == 1 ? 0f : col / (float)(resolution - 1);
                float v = -extentB + (2f * extentB) * tCol;

                double w = coefficients[0] * u * u +
                           coefficients[1] * v * v +
                           coefficients[2] * u * v +
                           coefficients[3] * u +
                           coefficients[4] * v +
                           coefficients[5];

                w = Math.Clamp(w, -extentNormal, extentNormal);

                var point = centroid + axisA * u + axisB * v + normal * (float)w;
                grid[row, col] = point;
            }
        }

        return new ValleySurface(grid);
    }

    private ValleySurface BuildPlanarValley(Vector3 centroid,
                                            Vector3 axisA,
                                            Vector3 axisB,
                                            float extentA,
                                            float extentB)
    {
        const int resolution = 20;
        var grid = new Vector3[resolution, resolution];

        for (int row = 0; row < resolution; row++)
        {
            float tRow = resolution == 1 ? 0f : row / (float)(resolution - 1);
            float u = -extentA + (2f * extentA) * tRow;

            for (int col = 0; col < resolution; col++)
            {
                float tCol = resolution == 1 ? 0f : col / (float)(resolution - 1);
                float v = -extentB + (2f * extentB) * tCol;
                grid[row, col] = centroid + axisA * u + axisB * v;
            }
        }

        return new ValleySurface(grid);
    }

    private static bool TryFitQuadraticSurface(List<Point3D> points,
                                               Vector3 centroid,
                                               Vector3 axisA,
                                               Vector3 axisB,
                                               Vector3 normal,
                                               out double[] coefficients)
    {
        coefficients = Array.Empty<double>();
        if (points.Count < 4)
            return false;

        const int terms = 6;
        var ata = new double[terms, terms];
        var atb = new double[terms];

        foreach (var point in points)
        {
            var diff = point.ToVector() - centroid;
            double u = Vector3.Dot(diff, axisA);
            double v = Vector3.Dot(diff, axisB);
            double w = Vector3.Dot(diff, normal);

            var row = new[]
            {
                u * u,
                v * v,
                u * v,
                u,
                v,
                1.0
            };

            for (int i = 0; i < terms; i++)
            {
                atb[i] += row[i] * w;
                for (int j = 0; j < terms; j++)
                    ata[i, j] += row[i] * row[j];
            }
        }

        double regularization = 1e-6 * points.Count;
        for (int i = 0; i < terms; i++)
            ata[i, i] += regularization;

        return TrySolveLinearSystem(ata, atb, out coefficients);
    }

    private static bool TrySolveLinearSystem(double[,] matrix, double[] rhs, out double[] solution)
    {
        int n = rhs.Length;
        solution = new double[n];

        var a = (double[,])matrix.Clone();
        var b = (double[])rhs.Clone();

        for (int pivot = 0; pivot < n; pivot++)
        {
            int bestRow = pivot;
            double bestValue = Math.Abs(a[pivot, pivot]);
            for (int row = pivot + 1; row < n; row++)
            {
                double value = Math.Abs(a[row, pivot]);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestRow = row;
                }
            }

            if (bestValue < 1e-12)
                return false;

            if (bestRow != pivot)
            {
                for (int col = pivot; col < n; col++)
                {
                    (a[pivot, col], a[bestRow, col]) = (a[bestRow, col], a[pivot, col]);
                }

                (b[pivot], b[bestRow]) = (b[bestRow], b[pivot]);
            }

            double diag = a[pivot, pivot];
            for (int col = pivot; col < n; col++)
                a[pivot, col] /= diag;
            b[pivot] /= diag;

            for (int row = 0; row < n; row++)
            {
                if (row == pivot)
                    continue;

                double factor = a[row, pivot];
                if (Math.Abs(factor) < 1e-12)
                    continue;

                for (int col = pivot; col < n; col++)
                    a[row, col] -= factor * a[pivot, col];
                b[row] -= factor * b[pivot];
            }
        }

        for (int i = 0; i < n; i++)
            solution[i] = b[i];
        return true;
    }

    private static float ComputeAutoScale(float rawRadius)
    {
        if (!float.IsFinite(rawRadius) || rawRadius <= 0f)
            return 1f;

        const float targetRadius = 1.4f;
        const float minimumRadius = 1e-3f;

        float clamped = Math.Max(rawRadius, minimumRadius);
        if (clamped >= targetRadius * 0.75f)
            return 1f;

        float scale = targetRadius / clamped;
        return Math.Clamp(scale, 1f, 500f);
    }

    private float ThicknessForNorm(double norm)
    {
        if (!double.IsFinite(norm) || !double.IsFinite(_minNorm) || !double.IsFinite(_maxNorm))
            return 3.5f;

        double range = _maxNorm - _minNorm;
        if (range < 1e-9)
            return 3.5f;

        double normalized = (norm - _minNorm) / range;
        normalized = Math.Clamp(normalized, 0.0, 1.0);
        return 2.5f + (float)normalized * 3.5f;
    }

    private SKColor ColorForAngle(double angle)
    {
        if (!double.IsFinite(angle))
            angle = 0.0;

        double min = double.IsFinite(_minAngle) ? _minAngle : 0.0;
        double max = double.IsFinite(_maxAngle) ? _maxAngle : min + 1.0;
        double range = max - min;
        if (range < 1e-6)
            range = 1.0;

        double normalized = (angle - min) / range;
        normalized = Math.Clamp(normalized, 0.0, 1.0);

        if (normalized < 0.5)
        {
            float t = (float)(normalized * 2.0);
            return InterpolateColor(AngleColdColor, AngleNeutralColor, t);
        }

        {
            float t = (float)((normalized - 0.5) * 2.0);
            return InterpolateColor(AngleNeutralColor, AngleHotColor, t);
        }
    }

    private static SKColor InterpolateColor(SKColor start, SKColor end, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        byte r = (byte)(start.Red + (end.Red - start.Red) * t);
        byte g = (byte)(start.Green + (end.Green - start.Green) * t);
        byte b = (byte)(start.Blue + (end.Blue - start.Blue) * t);
        byte a = (byte)(start.Alpha + (end.Alpha - start.Alpha) * t);
        return new SKColor(r, g, b, a);
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
                                 Vector3 up,
                                 float planeY,
                                 float radius)
    {
        float size = Math.Max(radius * 2.2f, 2f);
        int divisions = 6;

        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(24, 32, 48, 120) };
        using var strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(64, 82, 110, 120), StrokeWidth = 1f, IsAntialias = true };

        var corners = new[]
        {
            ProjectPoint(new Point3D(-size, planeY, -size, 0, 0), viewport, cameraPosition, forward, right, up),
            ProjectPoint(new Point3D(size, planeY, -size, 0, 0), viewport, cameraPosition, forward, right, up),
            ProjectPoint(new Point3D(size, planeY, size, 0, 0), viewport, cameraPosition, forward, right, up),
            ProjectPoint(new Point3D(-size, planeY, size, 0, 0), viewport, cameraPosition, forward, right, up)
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
            var startX = ProjectPoint(new Point3D(t, planeY, -size, 0, 0), viewport, cameraPosition, forward, right, up);
            var endX = ProjectPoint(new Point3D(t, planeY, size, 0, 0), viewport, cameraPosition, forward, right, up);
            if (startX.HasValue && endX.HasValue)
                canvas.DrawLine(startX.Value, endX.Value, strokePaint);

            var startZ = ProjectPoint(new Point3D(-size, planeY, t, 0, 0), viewport, cameraPosition, forward, right, up);
            var endZ = ProjectPoint(new Point3D(size, planeY, t, 0, 0), viewport, cameraPosition, forward, right, up);
            if (startZ.HasValue && endZ.HasValue)
                canvas.DrawLine(startZ.Value, endZ.Value, strokePaint);
        }
    }

    private void DrawAxes(SKCanvas canvas,
                          SKRect viewport,
                          Vector3 cameraPosition,
                          Vector3 forward,
                          Vector3 right,
                          Vector3 up,
                          float radius)
    {
        float length = Math.Max(radius * 1.2f, 1.2f);
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

    private static Vector3 ComputeCentroid(List<Point3D> points)
    {
        if (points.Count == 0)
            return Vector3.Zero;

        Vector3 sum = Vector3.Zero;
        foreach (var point in points)
            sum += point.ToVector();

        return sum / points.Count;
    }

    private static Vector3[]? ComputePrincipalAxes(List<Point3D> points)
    {
        if (points.Count == 0)
            return null;

        var centroid = ComputeCentroid(points);

        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;

        foreach (var point in points)
        {
            var diff = point.ToVector() - centroid;
            double dx = diff.X;
            double dy = diff.Y;
            double dz = diff.Z;

            xx += dx * dx;
            xy += dx * dy;
            xz += dx * dz;
            yy += dy * dy;
            yz += dy * dz;
            zz += dz * dz;
        }

        double inv = 1.0 / points.Count;
        xx *= inv;
        xy *= inv;
        xz *= inv;
        yy *= inv;
        yz *= inv;
        zz *= inv;

        var matrix = new double[3, 3]
        {
            { xx, xy, xz },
            { xy, yy, yz },
            { xz, yz, zz }
        };

        var decomposition = JacobiEigenDecomposition(matrix);
        if (decomposition == null)
            return null;

        var (vectors, values) = decomposition.Value;
        var order = new[] { 0, 1, 2 };
        Array.Sort(order, (a, b) => values[b].CompareTo(values[a]));

        return new[]
        {
            vectors[order[0]],
            vectors[order[1]],
            vectors[order[2]]
        };
    }

    private static (Vector3[] Vectors, double[] Values)? JacobiEigenDecomposition(double[,] matrix)
    {
        const int n = 3;
        var a = (double[,])matrix.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++)
            v[i, i] = 1.0;

        for (int iteration = 0; iteration < 25; iteration++)
        {
            int p = 0, q = 1;
            double max = Math.Abs(a[0, 1]);
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double value = Math.Abs(a[i, j]);
                    if (value > max)
                    {
                        max = value;
                        p = i;
                        q = j;
                    }
                }
            }

            if (max < 1e-10)
                break;

            double app = a[p, p];
            double aqq = a[q, q];
            double apq = a[p, q];

            double theta = (aqq - app) / (2.0 * apq);
            double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
            double c = 1.0 / Math.Sqrt(t * t + 1.0);
            double s = t * c;
            double tau = s / (1.0 + c);

            a[p, p] = app - t * apq;
            a[q, q] = aqq + t * apq;
            a[p, q] = a[q, p] = 0.0;

            for (int i = 0; i < n; i++)
            {
                if (i == p || i == q)
                    continue;

                double aip = a[i, p];
                double aiq = a[i, q];

                a[i, p] = aip - s * (aiq + tau * aip);
                a[p, i] = a[i, p];

                a[i, q] = aiq + s * (aip - tau * aiq);
                a[q, i] = a[i, q];
            }

            for (int i = 0; i < n; i++)
            {
                double vip = v[i, p];
                double viq = v[i, q];

                v[i, p] = vip - s * (viq + tau * vip);
                v[i, q] = viq + s * (vip - tau * viq);
            }
        }

        var eigenValues = new double[n];
        var eigenVectors = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            eigenValues[i] = a[i, i];
            var vec = new Vector3((float)v[0, i], (float)v[1, i], (float)v[2, i]);
            if (vec.LengthSquared() > 1e-9f)
                vec = Vector3.Normalize(vec);
            eigenVectors[i] = vec;
        }

        return (eigenVectors, eigenValues);
    }

    private static double ComputeGradientAngle(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0.0;

        double dot = 0.0;
        double normA = 0.0;
        double normB = 0.0;
        int length = Math.Min(a.Count, b.Count);

        for (int i = 0; i < length; i++)
        {
            double ai = a[i];
            double bi = b[i];
            dot += ai * bi;
            normA += ai * ai;
            normB += bi * bi;
        }

        if (normA < 1e-12 || normB < 1e-12)
            return 0.0;

        double denom = Math.Sqrt(normA * normB);
        if (denom < 1e-12)
            return 0.0;

        double cos = Math.Clamp(dot / denom, -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
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
