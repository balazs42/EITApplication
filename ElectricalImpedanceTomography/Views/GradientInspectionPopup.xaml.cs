using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace ElectricalImpedanceTomography.Views;

public partial class GradientInspectionPopup : Popup
{
    private readonly ReconstructionPageViewModel _viewModel;
    private readonly List<ReconstructionPageViewModel.GradientHistorySample> _sourceSamples = new();
    private readonly List<GradientDisplaySample> _displaySamples = new();
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
    private float _roll;
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
    private bool _suppressGradientSliderEvent;
    private bool _suppressOpacitySliderEvent;
    private float _surfaceDepthPrecision = 0.65f;
    private bool _suppressDepthSliderEvent;
    private bool _surfaceUpdatePending;
    private ValleySurface? _valleySurface;
    private float _surfaceOpacity = 0.68f;
    private bool _isGradientSliderDragging;
    private int? _pendingSliderSelection;
    private CancellationTokenSource? _surfaceRebuildCts;
    private bool _isAutoRotateEnabled;
    private RotationAxisOption _autoRotationAxis = RotationAxisOption.Y;
    private float _autoRotationSpeed = 20f;
    private bool _suppressRotationSpeedSliderEvent;
    private IDispatcherTimer? _autoRotateTimer;
    private DateTime _lastAutoRotateTick;

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
    private static readonly SKColor TerrainLowColor = SKColor.Parse("#142F50");
    private static readonly SKColor TerrainMidColor = SKColor.Parse("#1F5C7A");
    private static readonly SKColor TerrainHighColor = SKColor.Parse("#3AA6A0");
    private static readonly SKColor TerrainPeakColor = SKColor.Parse("#FFD166");
    private static readonly SKColor TerrainHighlightColor = SKColor.Parse("#FFEAA0");

    private readonly record struct Point3D(float X, float Y, float Z, int Iteration, double Norm)
    {
        public Vector3 ToVector() => new(X, Y, Z);
    }

    // Minimal ArrowSegment type
    private readonly record struct ArrowSegment(Point3D Start,
                                                Point3D End,
                                                float Norm,
                                                float Angle,
                                                int Index,
                                                int CollapsedCount);

    private readonly record struct GradientStep(int StartIndex,
                                                int EndIndex,
                                                Point3D Start,
                                                Point3D End,
                                                double Norm,
                                                double AngleDegrees,
                                                int CollapsedCount);

    private readonly record struct GradientDisplaySample(int SourceStartIndex,
                                                          int SourceEndIndex,
                                                          double Norm,
                                                          double? Angle,
                                                          int Iteration,
                                                          int FirstIteration,
                                                          int CollapsedCount,
                                                          int FrameIndex)
    {
        public bool IsAggregated => CollapsedCount > 1;
        public double EffectiveStepLength => Norm * Math.Max(1, CollapsedCount);
    }

    private enum RotationAxisOption
    {
        X = 0,
        Y = 1,
        Z = 2
    }

    private readonly record struct SampleProjection(float U,
                                                     float V,
                                                     float Height,
                                                     double Norm,
                                                     int CollapsedCount);

    private sealed class ValleySurface
    {
        public ValleySurface(Vector3[,] grid,
                             Vector3 centroid,
                             Vector3 normal,
                             float minElevation,
                             float maxElevation)
        {
            Grid = grid;
            Centroid = centroid;
            Normal = normal;
            MinElevation = minElevation;
            MaxElevation = maxElevation;
        }

        public Vector3[,] Grid { get; }
        public int Rows => Grid.GetLength(0);
        public int Columns => Grid.GetLength(1);
        public Vector3 Centroid { get; }
        public Vector3 Normal { get; }
        public float MinElevation { get; }
        public float MaxElevation { get; }
    }

    public GradientInspectionPopup(ReconstructionPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeRotationControls();

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
        CancelSurfaceRebuild();
        StopAutoRotate();
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
        MainThread.BeginInvokeOnMainThread(() => UpdateSelectionBySourceIndex(index));
    }

    private void LoadData()
    {
        var history = _viewModel.GetGradientHistorySnapshot();
        _sourceSamples.Clear();
        _sourceSamples.AddRange(history);

        _displaySamples.Clear();
        _displaySamples.AddRange(BuildDisplaySamples(history));

        RebuildTrajectory();

        if (_displaySamples.Count == 0)
        {
            UpdateSelection(-1);
        }
        else
        {
            int selectedSource = _viewModel.SelectedGradientIndex;
            int displayIndex = GetDisplayIndexForSourceIndex(selectedSource);
            if (displayIndex < 0)
                displayIndex = _displaySamples.Count - 1;
            UpdateSelection(displayIndex);
        }
    }

    private IEnumerable<GradientDisplaySample> BuildDisplaySamples(IReadOnlyList<ReconstructionPageViewModel.GradientHistorySample> source)
    {
        var results = new List<GradientDisplaySample>(source.Count);
        if (source.Count == 0)
            return results;

        const int maxDisplaySamples = 620;
        int blockSize = source.Count > maxDisplaySamples
            ? Math.Max(1, (int)Math.Ceiling(source.Count / (double)maxDisplaySamples))
            : 1;

        for (int start = 0; start < source.Count; start += blockSize)
        {
            int end = Math.Min(start + blockSize, source.Count) - 1;
            if (end < start)
                end = start;

            int collapsedCount = 0;
            double normSum = 0.0;
            double weightedAngleSum = 0.0;
            double weightSum = 0.0;

            for (int i = start; i <= end; i++)
            {
                var sample = source[i];
                int weight = Math.Max(1, sample.CollapsedCount);
                collapsedCount += weight;
                double norm = double.IsFinite(sample.Norm) ? sample.Norm : 0.0;
                normSum += norm * weight;

                if (sample.Angle.HasValue && double.IsFinite(sample.Angle.Value))
                {
                    double angleWeight = Math.Max(norm, 1e-6) * weight;
                    weightedAngleSum += sample.Angle.Value * angleWeight;
                    weightSum += angleWeight;
                }
            }

            double averageNorm = collapsedCount > 0 ? normSum / collapsedCount : 0.0;
            double? averageAngle = weightSum > 1e-6 ? weightedAngleSum / weightSum : (double?)null;

            var last = source[end];
            var first = source[start];

            results.Add(new GradientDisplaySample(start,
                                                  end,
                                                  averageNorm,
                                                  averageAngle,
                                                  last.Iteration,
                                                  first.FirstIteration,
                                                  collapsedCount,
                                                  last.FrameIndex));
        }

        return results;
    }

    private int GetDisplayIndexForSourceIndex(int sourceIndex)
    {
        if (sourceIndex < 0 || _displaySamples.Count == 0)
            return -1;

        for (int i = 0; i < _displaySamples.Count; i++)
        {
            var sample = _displaySamples[i];
            if (sourceIndex <= sample.SourceEndIndex)
                return i;
        }

        return _displaySamples.Count - 1;
    }

    private int GetSourceIndexForDisplayIndex(int displayIndex)
    {
        if (displayIndex < 0 || displayIndex >= _displaySamples.Count)
            return -1;

        return _displaySamples[displayIndex].SourceEndIndex;
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

        if (_displaySamples.Count == 0)
        {
            EmptyStateLabel.IsVisible = true;
            _trajectoryRadius = 1f;
            _planeY = 0f;
            _autoScale = 1f;
            _cameraDistance = Math.Max(_cameraDistance, _defaultCameraDistance);
            UpdateZoomSlider();
            UpdateScaleSlider();
            UpdateScaleLabel();
            UpdateSurfaceOpacitySlider();
            UpdateSurfaceDepthSlider();
            return;
        }

        EmptyStateLabel.IsVisible = false;

        // Build a synthetic 3D trajectory using gradient norm as step length
        // and the inter-step angle to steer the direction (yaw/pitch).
        double maxRadiusRaw = 0.0;

        // Precompute step angles from samples (angle at step i corresponds to vector from i-1 to i)
        for (int i = 1; i < _displaySamples.Count; i++)
        {
            double angle = _displaySamples[i].Angle ?? 0.0;
            if (!double.IsFinite(angle))
                angle = 0.0;
            _stepAngles.Add(angle);

            if (angle < _minAngle)
                _minAngle = angle;
            if (angle > _maxAngle)
                _maxAngle = angle;
        }

        // Initialize at origin with a forward direction along +X
        var current = new Vector3(0f, 0f, 0f);
        double yawRad = 0.0;   // rotation around Y axis
        double pitchRad = 0.0; // rotation around X axis

        const double yawFactor = Math.PI / 180.0 * 0.65;   // 0.65 rad per 100 deg
        const double pitchFactor = Math.PI / 180.0 * 0.35; // 0.35 rad per 100 deg

        for (int i = 0; i < _displaySamples.Count; i++)
        {
            var s = _displaySamples[i];

            // Track min/max norm for thickness mapping
            if (s.Norm < _minNorm)
                _minNorm = s.Norm;
            if (s.Norm > _maxNorm)
                _maxNorm = s.Norm;

            if (i > 0)
            {
                double angleDeg = _stepAngles[Math.Min(i - 1, _stepAngles.Count - 1)];

                // Derive a sign from local angle trend to introduce orientation variation
                double prev = i > 1 ? _stepAngles[i - 2] : angleDeg;
                int sign = Math.Sign(angleDeg - prev);
                if (sign == 0) sign = 1;

                yawRad += sign * angleDeg * yawFactor;
                pitchRad += sign * angleDeg * pitchFactor;

                // Compute unit direction from yaw/pitch
                float cx = (float)(Math.Cos(pitchRad) * Math.Cos(yawRad));
                float cy = (float)(Math.Sin(pitchRad));
                float cz = (float)(Math.Cos(pitchRad) * Math.Sin(yawRad));
                var dir = Vector3.Normalize(new Vector3(cx, cy, cz));

                // Step length proportional to norm (auto-scaling will normalize visually)
                float stepLen = (float)Math.Max(s.EffectiveStepLength, 0.0);
                current += dir * stepLen;
            }

            var rawPoint = new Point3D(current.X, current.Y, current.Z, s.Iteration, s.Norm);
            _rawPoints.Add(rawPoint);

            double radius = Math.Sqrt(current.X * current.X + current.Y * current.Y + current.Z * current.Z);
            if (radius > maxRadiusRaw)
                maxRadiusRaw = radius;
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
            float norm = (float)_displaySamples[i].Norm;
            float angle = (float)((i > 0 && i - 1 < _stepAngles.Count) ? _stepAngles[i - 1] : 0.0);
            _maxNormValue = Math.Max(_maxNormValue, norm);
            _maxAngleMagnitude = Math.Max(_maxAngleMagnitude, MathF.Abs(angle));
            int collapsedCount = Math.Max(1, _displaySamples[i].CollapsedCount);
            _arrowSegments.Add(new ArrowSegment(previous, _points[i], norm, angle, i, collapsedCount));
            previous = _points[i];
        }

        UpdateZoomSlider();
        UpdateScaleSlider();
        UpdateScaleLabel();
        UpdateSurfaceOpacitySlider();
        UpdateSurfaceDepthSlider();
        GradientCanvas.InvalidateSurface();
    }

    private void UpdateSelectionBySourceIndex(int sourceIndex)
    {
        int displayIndex = GetDisplayIndexForSourceIndex(sourceIndex);
        if (displayIndex < 0)
        {
            if (_displaySamples.Count == 0)
                UpdateSelection(-1);
            else
                UpdateSelection(_displaySamples.Count - 1);
        }
        else
        {
            UpdateSelection(displayIndex);
        }
    }

    private void UpdateSelection(int index)
    {
        if (index < 0 || index >= _displaySamples.Count)
        {
            _selectedIndex = -1;
            if (!_isGradientSliderDragging)
                _pendingSliderSelection = null;
            SelectionLabel.Text = _displaySamples.Count == 0
                ? "No gradient data yet"
                : "Select a sample to inspect";
            UpdateNavigationButtons();
            UpdateGradientSlider();
            RequestSurfaceUpdate();
            GradientCanvas.InvalidateSurface();
            return;
        }

        _selectedIndex = index;
        if (!_isGradientSliderDragging)
            _pendingSliderSelection = null;
        var sample = _displaySamples[index];
        string iterationLabel;
        if (sample.CollapsedCount > 1 && sample.FirstIteration != sample.Iteration)
        {
            iterationLabel = $"Iterations {sample.FirstIteration}–{sample.Iteration} ({sample.CollapsedCount} steps)";
        }
        else if (sample.CollapsedCount > 1)
        {
            iterationLabel = $"Iteration {sample.Iteration} ({sample.CollapsedCount} steps)";
        }
        else
        {
            iterationLabel = $"Iteration {sample.Iteration}";
        }

        string normDescriptor = sample.CollapsedCount > 1 ? "⟨‖∇J‖⟩" : "‖∇J‖";
        SelectionLabel.Text = $"{iterationLabel}: {normDescriptor} = {sample.Norm:F4}";
        UpdateNavigationButtons();
        UpdateGradientSlider();
        RequestSurfaceUpdate();
        GradientCanvas.InvalidateSurface();
    }

    private void RequestSelectionChange(int displayIndex, bool commitToViewModel = true)
    {
        if (displayIndex < 0 || displayIndex >= _displaySamples.Count)
            return;

        int sourceIndex = GetSourceIndexForDisplayIndex(displayIndex);
        if (sourceIndex < 0)
            return;

        if (!commitToViewModel)
        {
            UpdateSelection(displayIndex);
            return;
        }

        if (_viewModel.SelectedGradientIndex != sourceIndex)
        {
            _viewModel.SetSelectedGradientIndex(sourceIndex);
        }
        else
        {
            UpdateSelection(displayIndex);
        }
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
            double norm = _displaySamples[i].Norm;
            int collapsedCount = Math.Max(1, _displaySamples[i].CollapsedCount);
            _steps.Add(new GradientStep(i - 1, i, _points[i - 1], _points[i], norm, angle, collapsedCount));
        }

        if (minY == double.MaxValue)
            minY = 0.0;

        _trajectoryRadius = maxRadius > 1e-6 ? (float)maxRadius : 1f;
        _planeY = (float)(minY - 0.2 * _trajectoryRadius);
        _defaultCameraDistance = Math.Max(_trajectoryRadius * 3f, 4f);
        if (_cameraDistance <= 0f)
            _cameraDistance = _defaultCameraDistance;

        RequestSurfaceUpdate();
    }

    private void UpdateNavigationButtons()
    {
        if (PrevStepButton == null || NextStepButton == null)
            return;

        bool hasSamples = _displaySamples.Count > 0;

        bool canStepBack = hasSamples && _selectedIndex > 0;
        bool canStepForward = hasSamples && ((_selectedIndex >= 0 && _selectedIndex < _displaySamples.Count - 1) || _selectedIndex < 0);

        SetNavigationButtonState(PrevStepButton, canStepBack);
        SetNavigationButtonState(NextStepButton, canStepForward);
    }

    private static void SetNavigationButtonState(Image button, bool enabled)
    {
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1.0 : 0.35;
    }

    private void UpdateGradientSlider()
    {
        if (GradientSlider == null)
            return;

        _suppressGradientSliderEvent = true;

        if (_displaySamples.Count == 0)
        {
            GradientSlider.Minimum = 0;
            GradientSlider.Maximum = 0;
            GradientSlider.Value = 0;
            GradientSlider.IsEnabled = false;
        }
        else
        {
            GradientSlider.Minimum = 0;
            GradientSlider.Maximum = Math.Max(0, _displaySamples.Count - 1);
            double target = _selectedIndex >= 0 ? _selectedIndex : _displaySamples.Count - 1;
            GradientSlider.Value = target;
            GradientSlider.IsEnabled = _displaySamples.Count > 1;
        }

        _suppressGradientSliderEvent = false;
        UpdateGradientIndexLabel();
    }

    private void UpdateGradientIndexLabel()
    {
        if (GradientIndexLabel == null)
            return;

        if (_displaySamples.Count == 0)
        {
            GradientIndexLabel.Text = "0 / 0";
            return;
        }

        int current = _selectedIndex >= 0 ? _selectedIndex + 1 : _displaySamples.Count;
        GradientIndexLabel.Text = $"{current} / {_displaySamples.Count}";
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
        ScaleSlider.IsEnabled = _displaySamples.Count > 0;
        _suppressScaleSliderEvent = false;
    }

    private void UpdateScaleLabel()
    {
        if (ScaleValueLabel == null)
            return;

        float total = Math.Clamp(_autoScale * _manualScale, 0.01f, 1000f);
        ScaleValueLabel.Text = _displaySamples.Count == 0
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

    private void OnGradientSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_suppressGradientSliderEvent)
            return;

        if (_displaySamples.Count == 0)
            return;

        int target = (int)Math.Round(e.NewValue);
        target = Math.Clamp(target, 0, _displaySamples.Count - 1);

        if (Math.Abs(e.NewValue - target) > double.Epsilon)
        {
            _suppressGradientSliderEvent = true;
            GradientSlider.Value = target;
            _suppressGradientSliderEvent = false;
        }

        bool commitToViewModel = !_isGradientSliderDragging;

        if (_isGradientSliderDragging)
            _pendingSliderSelection = target;
        else
            _pendingSliderSelection = null;

        if (_selectedIndex != target)
            RequestSelectionChange(target, commitToViewModel);

        UpdateGradientIndexLabel();
    }

    private void OnGradientSliderDragStarted(object? sender, EventArgs e)
    {
        _isGradientSliderDragging = true;
        _surfaceUpdatePending = false;
    }

    private void OnGradientSliderDragCompleted(object? sender, EventArgs e)
    {
        _isGradientSliderDragging = false;

        if (_displaySamples.Count == 0)
        {
            _pendingSliderSelection = null;
            return;
        }

        int target;
        if (_pendingSliderSelection.HasValue)
        {
            target = _pendingSliderSelection.Value;
        }
        else
        {
            double sliderValue = GradientSlider?.Value ?? _selectedIndex;
            target = (int)Math.Round(sliderValue);
        }

        target = Math.Clamp(target, 0, _displaySamples.Count - 1);
        _pendingSliderSelection = null;

        if (_selectedIndex != target
            || _viewModel.SelectedGradientIndex != GetSourceIndexForDisplayIndex(target))
        {
            RequestSelectionChange(target, commitToViewModel: true);
        }

        UpdateGradientIndexLabel();
        ProcessPendingSurfaceUpdate();
    }

    private void UpdateSurfaceOpacitySlider()
    {
        if (SurfaceOpacitySlider == null)
            return;

        _suppressOpacitySliderEvent = true;
        SurfaceOpacitySlider.Minimum = 0.05;
        SurfaceOpacitySlider.Maximum = 1.0;
        SurfaceOpacitySlider.Value = Math.Clamp(_surfaceOpacity, 0.05f, 1f);
        SurfaceOpacitySlider.IsEnabled = _displaySamples.Count > 0;
        _suppressOpacitySliderEvent = false;
        UpdateSurfaceOpacityLabel();
    }

    private void UpdateSurfaceOpacityLabel()
    {
        if (SurfaceOpacityValueLabel == null)
            return;

        SurfaceOpacityValueLabel.Text = $"{_surfaceOpacity:0%}";
    }

    private void UpdateSurfaceDepthSlider()
    {
        if (ValleyDepthSlider == null)
            return;

        _suppressDepthSliderEvent = true;
        ValleyDepthSlider.Minimum = 0.25;
        ValleyDepthSlider.Maximum = 1.0;
        ValleyDepthSlider.Value = Math.Clamp(_surfaceDepthPrecision, 0.25f, 1f);
        ValleyDepthSlider.IsEnabled = _displaySamples.Count > 0;
        _suppressDepthSliderEvent = false;
        UpdateSurfaceDepthLabel();
    }

    private void UpdateSurfaceDepthLabel()
    {
        if (ValleyDepthValueLabel == null)
            return;

        ValleyDepthValueLabel.Text = $"{_surfaceDepthPrecision:0%}";
    }

    private void OnValleyDepthSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_suppressDepthSliderEvent)
            return;

        _surfaceDepthPrecision = (float)Math.Clamp(e.NewValue, 0.25, 1.0);
        UpdateSurfaceDepthLabel();
        RequestSurfaceUpdate();
        GradientCanvas.InvalidateSurface();
    }

    private void CancelSurfaceRebuild()
    {
        var existing = Interlocked.Exchange(ref _surfaceRebuildCts, null);
        if (existing != null)
        {
            try
            {
                existing.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore - the token source was already disposed elsewhere.
            }
        }
    }

    private void OnSurfaceOpacitySliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_suppressOpacitySliderEvent)
            return;

        _surfaceOpacity = (float)Math.Clamp(e.NewValue, 0.05, 1.0);
        UpdateSurfaceOpacityLabel();
        GradientCanvas.InvalidateSurface();
    }

    private void RequestSurfaceUpdate()
    {
        if (_isGradientSliderDragging)
        {
            _surfaceUpdatePending = true;
            return;
        }

        _surfaceUpdatePending = false;
        UpdateValleySurface();
    }

    private void ProcessPendingSurfaceUpdate()
    {
        if (!_surfaceUpdatePending)
            return;

        _surfaceUpdatePending = false;
        UpdateValleySurface();
    }

    private void UpdateValleySurface()
    {
        int visibleCount = GetVisibleSampleCount();
        if (visibleCount < 4)
        {
            CancelSurfaceRebuild();
            _valleySurface = null;
            return;
        }

        var pointSnapshot = _points.Take(visibleCount).ToArray();
        var sampleSnapshot = _displaySamples.Take(visibleCount).ToArray();
        if (pointSnapshot.Length < 4)
        {
            CancelSurfaceRebuild();
            _valleySurface = null;
            return;
        }

        CancelSurfaceRebuild();

        var cts = new CancellationTokenSource();
        _surfaceRebuildCts = cts;
        float depthPrecision = _surfaceDepthPrecision;

        Task.Run(() =>
        {
            ValleySurface? surface = null;
            try
            {
                surface = BuildValleySurfaceSnapshot(pointSnapshot,
                                                     sampleSnapshot,
                                                     _trajectoryRadius,
                                                     depthPrecision,
                                                     cts.Token);
            }
            catch (OperationCanceledException)
            {
                surface = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GradientInspectionPopup] Surface rebuild failed: {ex}");
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(_surfaceRebuildCts, cts))
            {
                cts.Dispose();
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_surfaceRebuildCts, cts))
                {
                    cts.Dispose();
                    return;
                }

                _valleySurface = surface;
                _surfaceRebuildCts = null;
                GradientCanvas.InvalidateSurface();
                cts.Dispose();
            });
        });
    }

    private ValleySurface? BuildValleySurfaceSnapshot(Point3D[] points,
                                                      GradientDisplaySample[] samples,
                                                      float trajectoryRadius,
                                                      float depthPrecision,
                                                      CancellationToken token)
    {
        if (points.Length < 4 || samples.Length == 0)
            return null;

        float precision = Math.Clamp(depthPrecision, 0.25f, 1f);
        var fitPoints = new List<Point3D>(points);
        var vectors = new Vector3[points.Length];
        Vector3 centroid = Vector3.Zero;
        for (int i = 0; i < points.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var vector = points[i].ToVector();
            vectors[i] = vector;
            centroid += vector;
        }

        centroid /= points.Length;

        Vector3 axisA = vectors[^1] - vectors[0];
        if (axisA.LengthSquared() < 1e-6f)
        {
            axisA = Vector3.Zero;
            for (int i = 1; i < vectors.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                axisA += vectors[i] - vectors[i - 1];
            }
        }

        axisA = axisA.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(axisA);

        Vector3 lateralAccum = Vector3.Zero;
        for (int i = 0; i < vectors.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var diff = vectors[i] - centroid;
            var projected = axisA * Vector3.Dot(diff, axisA);
            lateralAccum += diff - projected;
        }

        Vector3 axisB = lateralAccum.LengthSquared() < 1e-6f
            ? Vector3.Normalize(Vector3.Cross(axisA, Vector3.UnitY))
            : Vector3.Normalize(lateralAccum);
        if (axisB.LengthSquared() < 1e-6f)
            axisB = Vector3.Normalize(Vector3.Cross(axisA, Vector3.UnitZ));
        if (axisB.LengthSquared() < 1e-6f)
            axisB = Vector3.UnitY;

        Vector3 normal = Vector3.Normalize(Vector3.Cross(axisA, axisB));
        if (normal.LengthSquared() < 1e-6f)
            normal = Vector3.UnitY;

        float maxAbsU = 0f;
        float maxAbsV = 0f;
        float maxAbsW = 0f;
        var projections = new List<SampleProjection>(points.Length);

        for (int i = 0; i < points.Length; i++)
        {
            token.ThrowIfCancellationRequested();

            var diff = vectors[i] - centroid;
            float u = Vector3.Dot(diff, axisA);
            float v = Vector3.Dot(diff, axisB);
            float w = Vector3.Dot(diff, normal);

            maxAbsU = MathF.Max(maxAbsU, MathF.Abs(u));
            maxAbsV = MathF.Max(maxAbsV, MathF.Abs(v));
            maxAbsW = MathF.Max(maxAbsW, MathF.Abs(w));

            double norm = i < samples.Length ? samples[i].Norm : 0.0;
            int collapsed = i < samples.Length ? Math.Max(1, samples[i].CollapsedCount) : 1;

            projections.Add(new SampleProjection(u, v, w, norm, collapsed));
        }

        float extentA = MathF.Max(maxAbsU * (1.1f + 0.4f * precision), trajectoryRadius * (0.45f + 0.3f * precision));
        float extentB = MathF.Max(maxAbsV * (1.1f + 0.4f * precision), trajectoryRadius * (0.45f + 0.3f * precision));
        float extentNormalBase = MathF.Max(maxAbsW * (1.2f + 0.6f * precision), MathF.Max(trajectoryRadius * (0.2f + 0.5f * precision), 0.25f));
        float extentNormal = extentNormalBase * (0.9f + 0.65f * precision);

        if (!TryFitQuadraticSurface(fitPoints, centroid, axisA, axisB, normal, out var coefficients))
            return BuildPlanarValley(centroid, axisA, axisB, extentA, extentB);

        return BuildEnhancedQuadraticValley(coefficients,
                                            projections,
                                            centroid,
                                            axisA,
                                            axisB,
                                            normal,
                                            extentA,
                                            extentB,
                                            extentNormal,
                                            precision,
                                            token);
    }

    private ValleySurface BuildEnhancedQuadraticValley(double[] coefficients,
                                                       IReadOnlyList<SampleProjection> samples,
                                                       Vector3 centroid,
                                                       Vector3 axisA,
                                                       Vector3 axisB,
                                                       Vector3 normal,
                                                       float extentA,
                                                       float extentB,
                                                       float extentNormal,
                                                       float precision,
                                                       CancellationToken token)
    {
        float precisionClamped = Math.Clamp(precision, 0.25f, 1f);
        int baseResolution = Math.Clamp(samples.Count / 6, 32, 72);
        int resolution = Math.Clamp((int)(baseResolution * (0.9f + 0.7f * precisionClamped)), 32, 110);
        var grid = new Vector3[resolution, resolution];
        float minElevation = float.PositiveInfinity;
        float maxElevation = float.NegativeInfinity;

        double minNorm = double.PositiveInfinity;
        double maxNorm = double.NegativeInfinity;
        foreach (var sample in samples)
        {
            token.ThrowIfCancellationRequested();
            if (!double.IsFinite(sample.Norm))
                continue;

            if (sample.Norm < minNorm)
                minNorm = sample.Norm;
            if (sample.Norm > maxNorm)
                maxNorm = sample.Norm;
        }

        if (!double.IsFinite(minNorm) || !double.IsFinite(maxNorm))
        {
            minNorm = 0.0;
            maxNorm = 1.0;
        }

        double normRange = Math.Max(1e-9, maxNorm - minNorm);

        var residuals = new float[samples.Count];
        var weights = new float[samples.Count];
        float maxResidual = 0f;

        for (int i = 0; i < samples.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var sample = samples[i];

            double baseHeight = EvaluateQuadratic(coefficients, sample.U, sample.V);
            float residual = (float)(sample.Height - baseHeight);
            float depthStrength = 0.65f + 0.9f * precisionClamped;
            residuals[i] = residual * depthStrength;
            maxResidual = MathF.Max(maxResidual, MathF.Abs(residuals[i]));

            double normFactor = double.IsFinite(sample.Norm)
                ? Math.Clamp((sample.Norm - minNorm) / normRange, 0.0, 1.0)
                : 0.0;

            float collapsedFactor = MathF.Log(1f + MathF.Max(sample.CollapsedCount, 1));
            float normWeight = (float)normFactor * (0.55f + 0.9f * precisionClamped);
            float collapsedWeight = collapsedFactor * (0.2f + 0.35f * precisionClamped);
            weights[i] = 0.55f + 0.35f * (1f - precisionClamped) + normWeight + collapsedWeight;
        }

        if (maxResidual > extentNormal)
        {
            float scale = MathF.Min(1f, extentNormal / MathF.Max(maxResidual, 1e-5f));
            for (int i = 0; i < residuals.Length; i++)
                residuals[i] *= scale;
        }

        float kernelRadiusBase = ComputeKernelRadius(samples, extentA, extentB);
        float kernelRadius = MathF.Max(kernelRadiusBase * (1.2f - 0.5f * precisionClamped), 0.08f);
        float kernelRadiusSq = kernelRadius * kernelRadius;
        float innerRadiusSq = kernelRadiusSq * (0.25f + 0.25f * precisionClamped);
        float midRadiusSq = kernelRadiusSq * (0.7f + 0.2f * precisionClamped);
        float anchorRadiusSq = kernelRadiusSq * (0.36f + 0.28f * precisionClamped);
        float fadeRadius = kernelRadius * (1.55f - 0.35f * precisionClamped);
        double residualStrength = 0.85 + 0.95 * precisionClamped;

        for (int row = 0; row < resolution; row++)
        {
            token.ThrowIfCancellationRequested();
            float tRow = resolution == 1 ? 0f : row / (float)(resolution - 1);
            float u = -extentA + (2f * extentA) * tRow;

            for (int col = 0; col < resolution; col++)
            {
                float tCol = resolution == 1 ? 0f : col / (float)(resolution - 1);
                float v = -extentB + (2f * extentB) * tCol;

                double baseHeight = EvaluateQuadratic(coefficients, u, v);
                double adjusted = 0.0;
                double weightSum = 0.0;
                double nearest = double.PositiveInfinity;
                double anchorHeight = 0.0;
                double anchorWeight = 0.0;

                for (int i = 0; i < samples.Count; i++)
                {
                    var sample = samples[i];
                    float du = u - sample.U;
                    float dv = v - sample.V;
                    double distanceSq = du * du + dv * dv;
                    double distance = Math.Sqrt(distanceSq);
                    if (distance < nearest)
                        nearest = distance;

                    double influence = Math.Exp(-distanceSq / Math.Max(1e-6f, kernelRadiusSq * (0.75f - 0.2f * precisionClamped)));
                    if (distanceSq <= innerRadiusSq)
                    {
                        influence *= 1.6 + 1.4 * precisionClamped;
                    }
                    else if (distanceSq <= midRadiusSq)
                    {
                        influence *= 1.05 + 0.95 * precisionClamped;
                    }
                    else
                    {
                        influence *= 0.55 + 0.6 * (1f - precisionClamped);
                    }

                    double weightedInfluence = influence * weights[i];
                    adjusted += residuals[i] * weightedInfluence;
                    weightSum += weightedInfluence;

                    if (distanceSq <= anchorRadiusSq)
                    {
                        double anchorInfluence = Math.Exp(-distanceSq / Math.Max(1e-6f, anchorRadiusSq * (0.75f - 0.25f * precisionClamped)));
                        anchorHeight += sample.Height * anchorInfluence;
                        anchorWeight += anchorInfluence;
                    }
                }

                double residualAdjustment = weightSum > 1e-6 ? (adjusted / weightSum) * residualStrength : 0.0;

                if (nearest > fadeRadius)
                {
                    double fade = Math.Exp(-Math.Pow((nearest - fadeRadius) / (fadeRadius * (0.85 + 0.4 * (1f - precisionClamped)) + 1e-6f), 2));
                    residualAdjustment *= fade;
                }

                double height = baseHeight + residualAdjustment;

                if (anchorWeight > 1e-6)
                {
                    double anchorAverage = anchorHeight / anchorWeight;
                    double blend = 0.2 + 0.55 * precisionClamped;
                    height = height * (1.0 - blend) + anchorAverage * blend;
                }

                height = Math.Clamp(height, -extentNormal, extentNormal);

                var point = centroid + axisA * u + axisB * v + normal * (float)height;
                grid[row, col] = point;

                float elevation = Vector3.Dot(point - centroid, normal);
                if (elevation < minElevation)
                    minElevation = elevation;
                if (elevation > maxElevation)
                    maxElevation = elevation;
            }
        }

        return new ValleySurface(grid, centroid, normal, minElevation, maxElevation);
    }

    private static float ComputeKernelRadius(IReadOnlyList<SampleProjection> samples,
                                             float extentA,
                                             float extentB)
    {
        if (samples.Count < 2)
            return MathF.Max(MathF.Max(extentA, extentB) * 0.25f, 0.1f);

        float totalDistance = 0f;
        int count = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            float du = samples[i].U - samples[i - 1].U;
            float dv = samples[i].V - samples[i - 1].V;
            float distance = MathF.Sqrt(du * du + dv * dv);
            if (distance > 1e-4f)
            {
                totalDistance += distance;
                count++;
            }
        }

        float average = count > 0 ? totalDistance / count : MathF.Max(extentA, extentB) / MathF.Max(samples.Count, 1);
        float maxExtent = MathF.Max(extentA, extentB);
        float radius = Math.Clamp(average * 2.4f, maxExtent * 0.15f, maxExtent * 0.8f);
        return MathF.Max(radius, 0.1f);
    }

    private static double EvaluateQuadratic(double[] coefficients, float u, float v)
    {
        return coefficients[0] * u * u +
               coefficients[1] * v * v +
               coefficients[2] * u * v +
               coefficients[3] * u +
               coefficients[4] * v +
               coefficients[5];
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
        ZoomSlider.IsEnabled = _displaySamples.Count > 0;
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
        _roll = 0f;
        _lastAutoRotateTick = DateTime.UtcNow;
        UpdateZoomSlider();
        GradientCanvas.InvalidateSurface();
    }

    private void OnPreviousGradientTapped(object? sender, TappedEventArgs e)
    {
        if (_displaySamples.Count == 0)
            return;

        int target = _selectedIndex <= 0 ? 0 : _selectedIndex - 1;
        if (_selectedIndex != target)
            RequestSelectionChange(target);
    }

    private void OnNextGradientTapped(object? sender, TappedEventArgs e)
    {
        if (_displaySamples.Count == 0)
            return;

        int target = _selectedIndex < 0 ? 0 : Math.Min(_selectedIndex + 1, _displaySamples.Count - 1);
        if (_selectedIndex != target)
            RequestSelectionChange(target);
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

        // Environment
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
                _lastAutoRotateTick = DateTime.UtcNow;
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
        if (_displaySamples.Count == 0)
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
            RequestSelectionChange(bestIndex);
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

        if (_surfaceOpacity <= 0.02f)
            return;

        var surface = _valleySurface;
        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f, IsAntialias = true };
        using var contourPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };

        float elevationRange = Math.Max(surface.MaxElevation - surface.MinElevation, 1e-4f);
        byte fillAlpha = (byte)Math.Clamp(_surfaceOpacity * 255f, 0f, 255f);
        byte strokeAlpha = (byte)Math.Clamp(_surfaceOpacity * 210f, 0f, 255f);
        contourPaint.Color = SecondaryPlaneStroke.WithAlpha((byte)Math.Clamp(_surfaceOpacity * 180f, 0f, 255f));

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

                var center = (p00 + p10 + p11 + p01) * 0.25f;
                float elevation = Vector3.Dot(center - surface.Centroid, surface.Normal);
                float normalized = (elevation - surface.MinElevation) / elevationRange;
                var terrainColor = EvaluateTerrainColor(normalized);
                float highlight = ComputeHighlightFactor(center);
                var blended = InterpolateColor(terrainColor, TerrainHighlightColor, highlight);

                fillPaint.Color = blended.WithAlpha(fillAlpha);
                strokePaint.Color = blended.WithAlpha(strokeAlpha);

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

    private float ComputeHighlightFactor(Vector3 point)
    {
        float distance = DistanceToTrajectory(point);
        if (!float.IsFinite(distance))
            return 0f;

        float sigma = Math.Max(_trajectoryRadius * 0.35f, 0.5f);
        if (sigma < 1e-3f)
            sigma = 0.5f;

        float value = MathF.Exp(-(distance * distance) / (2f * sigma * sigma));
        return Math.Clamp(value, 0f, 1f);
    }

    private float DistanceToTrajectory(Vector3 point)
    {
        if (_points.Count == 0)
            return 0f;

        if (_points.Count == 1)
            return Vector3.Distance(point, _points[0].ToVector());

        float minDistance = float.MaxValue;

        for (int i = 1; i < _points.Count; i++)
        {
            var a = _points[i - 1].ToVector();
            var b = _points[i].ToVector();
            float distance = DistancePointToSegment(point, a, b);
            if (distance < minDistance)
                minDistance = distance;
        }

        return float.IsFinite(minDistance) ? minDistance : 0f;
    }

    private static float DistancePointToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float lengthSq = Vector3.Dot(ab, ab);
        if (lengthSq < 1e-6f)
            return Vector3.Distance(point, a);

        float t = Vector3.Dot(point - a, ab) / lengthSq;
        t = Math.Clamp(t, 0f, 1f);
        var closest = a + ab * t;
        return Vector3.Distance(point, closest);
    }

    private ValleySurface BuildPlanarValley(Vector3 centroid,
                                            Vector3 axisA,
                                            Vector3 axisB,
                                            float extentA,
                                            float extentB)
    {
        const int resolution = 20;
        var grid = new Vector3[resolution, resolution];

        Vector3 normal = Vector3.Normalize(Vector3.Cross(axisA, axisB));
        if (normal.LengthSquared() < 1e-6f)
            normal = Vector3.UnitY;

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

        return new ValleySurface(grid, centroid, normal, 0f, 0f);
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

    private static SKColor EvaluateTerrainColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.33f)
        {
            float local = t / 0.33f;
            return InterpolateColor(TerrainLowColor, TerrainMidColor, local);
        }

        if (t < 0.66f)
        {
            float local = (t - 0.33f) / 0.33f;
            return InterpolateColor(TerrainMidColor, TerrainHighColor, local);
        }

        float last = (t - 0.66f) / 0.34f;
        return InterpolateColor(TerrainHighColor, TerrainPeakColor, last);
    }

    private (Vector3 Position, Vector3 Forward, Vector3 Right, Vector3 Up) GetCameraFrame()
    {
        float yawRad = DegreesToRadians(_yaw);
        float pitchRad = DegreesToRadians(_pitch);
        float rollRad = DegreesToRadians(_roll);
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

        if (Math.Abs(_roll) > 1e-3f)
        {
            var rotation = Matrix4x4.CreateFromAxisAngle(forward, rollRad);
            right = Vector3.Normalize(Vector3.TransformNormal(right, rotation));
            up = Vector3.Normalize(Vector3.Cross(right, forward));
        }

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

    private static float NormalizeAngle(float degrees)
    {
        float normalized = degrees % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }

    private static float NormalizeAngleSigned(float degrees)
    {
        float normalized = NormalizeAngle(degrees);
        return normalized > 180f ? normalized - 360f : normalized;
    }

    private void InitializeRotationControls()
    {
        _autoRotateTimer = Dispatcher.CreateTimer();
        _autoRotateTimer.Interval = TimeSpan.FromMilliseconds(1);
        _autoRotateTimer.Tick += OnAutoRotateTimerTick;

        if (AutoRotateAxisPicker != null)
            AutoRotateAxisPicker.SelectedIndex = (int)_autoRotationAxis;

        if (AutoRotateSpeedSlider != null)
        {
            _suppressRotationSpeedSliderEvent = true;
            AutoRotateSpeedSlider.Value = _autoRotationSpeed;
            _suppressRotationSpeedSliderEvent = false;
        }

        UpdateAutoRotateSpeedLabel();
        UpdateAutoRotateSliderState();
    }

    private void OnAutoRotateToggled(object? sender, ToggledEventArgs e)
    {
        _isAutoRotateEnabled = e.Value;
        _lastAutoRotateTick = DateTime.UtcNow;
        UpdateAutoRotateSliderState();
        if (_isAutoRotateEnabled)
            StartAutoRotate();
        else
            StopAutoRotate();
    }

    private void OnAutoRotateAxisChanged(object? sender, EventArgs e)
    {
        if (AutoRotateAxisPicker?.SelectedIndex is int index && index >= 0)
        {
            _autoRotationAxis = (RotationAxisOption)index;
            _lastAutoRotateTick = DateTime.UtcNow;
        }
    }

    private void OnAutoRotateSpeedChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_suppressRotationSpeedSliderEvent)
            return;

        _autoRotationSpeed = (float)e.NewValue;
        UpdateAutoRotateSpeedLabel();
        _lastAutoRotateTick = DateTime.UtcNow;
    }

    private void UpdateAutoRotateSpeedLabel()
    {
        if (AutoRotateSpeedValueLabel != null)
            AutoRotateSpeedValueLabel.Text = $"{_autoRotationSpeed:0}°/s";
    }

    private void UpdateAutoRotateSliderState()
    {
        if (AutoRotateSpeedSlider != null)
            AutoRotateSpeedSlider.IsEnabled = true;
        if (AutoRotateAxisPicker != null)
            AutoRotateAxisPicker.IsEnabled = true;
        if (AutoRotateSwitch != null && AutoRotateSwitch.IsToggled != _isAutoRotateEnabled)
            AutoRotateSwitch.IsToggled = _isAutoRotateEnabled;
    }

    private void StartAutoRotate()
    {
        if (_autoRotateTimer == null)
            return;

        _lastAutoRotateTick = DateTime.UtcNow;
        if (!_autoRotateTimer.IsRunning)
            _autoRotateTimer.Start();
    }

    private void StopAutoRotate()
    {
        if (_autoRotateTimer == null)
            return;

        if (_autoRotateTimer.IsRunning)
            _autoRotateTimer.Stop();
    }

    private void OnAutoRotateTimerTick(object? sender, EventArgs e)
    {
        if (!_isAutoRotateEnabled)
            return;

        var now = DateTime.UtcNow;
        if (_lastAutoRotateTick == default)
        {
            _lastAutoRotateTick = now;
            return;
        }

        double elapsedSeconds = (now - _lastAutoRotateTick).TotalSeconds;
        _lastAutoRotateTick = now;

        if (elapsedSeconds <= 0)
            return;

        float delta = _autoRotationSpeed * (float)elapsedSeconds;
        switch (_autoRotationAxis)
        {
            case RotationAxisOption.X:
                _pitch = NormalizeAngleSigned(_pitch + delta);
                break;
            case RotationAxisOption.Y:
                _yaw = NormalizeAngle(_yaw + delta);
                break;
            case RotationAxisOption.Z:
                _roll = NormalizeAngle(_roll + delta);
                break;
        }

        GradientCanvas?.InvalidateSurface();
    }
}
