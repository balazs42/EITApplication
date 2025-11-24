using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using System.Collections.Specialized;
using System.Linq;
using Utility.Classes.Application;
using Utility.Classes.Configurations.ReconstructionConfiguration;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace ElectricalImpedanceTomography.Views
{
    /// <summary>
    /// Code-behind for the Reconstruction Configuration Page.
    /// Handles dynamic UI generation, block dragging, and connection creation (drag-and-drop).
    /// </summary>
    public partial class ReconstructionConfigurationPage : ContentPage
    {
        private readonly ReconstructionConfigurationPageViewModel _viewModel;
        private readonly Dictionary<string, Point> _dragStartPositions = new();

        private Point? _tempConnectionStart;
        private Point? _tempConnectionEnd;
        private ReconstructionConfigurationBlock? _tempConnectionSource;

        // Selection Rectangle State
        private Point? _selectionStart;
        private bool _isSelecting = false;

        // Dragging state
        private ReconstructionConfigurationBlock? _draggedBlock;
        private Point _dragStartPoint;
        private Point _dragStartBlockPosition;

        private const double PortCenterY = 30;
        private const double OutputPortXOffset = 214;
        private const double InputPortXOffset = 0;
        private const double BlockWidth = 214;
        private const double BlockHeight = 80;

        // Mesh preview drawing helpers
        private readonly SKPaint _lbmFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black };
        private readonly SKPaint _lbmWall = new() { Style = SKPaintStyle.Fill, Color = SKColors.White };
        private readonly SKPaint _lbmElectrode = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange };
        private readonly SKPaint _lbmStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };

        private readonly SKPaint _femStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };
        private readonly SKPaint _femFill = new() { Style = SKPaintStyle.Fill };
        private readonly SKPaint _electrodeFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Yellow };
        private readonly SKPaint _electrodeSegmentStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Gold, StrokeWidth = 3, IsAntialias = true };

        public ReconstructionConfigurationPage()
        {
            InitializeComponent();
            _viewModel = new ReconstructionConfigurationPageViewModel();
            BindingContext = _viewModel;

            _viewModel.Blocks.CollectionChanged += OnBlocksChanged;
            _viewModel.Connections.CollectionChanged += (s, e) => ConnectionsCanvas.InvalidateSurface();

            RefreshNodeContainer();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            MeshPreviewCanvas?.InvalidateSurface();
        }

        private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshNodeContainer();
            ConnectionsCanvas.InvalidateSurface();
        }

        private void RefreshNodeContainer()
        {
            NodeContainer.Children.Clear();
            foreach (var block in _viewModel.Blocks)
            {
                var view = CreateBlockView(block);
                NodeContainer.Children.Add(view);
            }
        }

        private View CreateBlockView(ReconstructionConfigurationBlock block)
        {
            var border = new Border
            {
                Stroke = Color.FromArgb("#555"),
                StrokeThickness = 1,
                BackgroundColor = Color.FromArgb("#3A3A4E"),
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                WidthRequest = 200,
                Shadow = new Shadow { Brush = Brush.Black, Offset = new Point(0, 5), Opacity = 0.4f, Radius = 10 }
            };

            var headerColor = Color.FromArgb(block.IconColor);

            var mainStack = new VerticalStackLayout();
            var headerGrid = new Grid { BackgroundColor = headerColor.WithAlpha(0.15f), Padding = 10, HeightRequest = 40 };
            headerGrid.Add(new BoxView { Color = headerColor, WidthRequest = 4, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Fill });
            var titleLabel = new Label
            {
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#F0F0F0"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(10, 0, 0, 0),
                BindingContext = block
            };
            titleLabel.SetBinding(Label.TextProperty, nameof(ReconstructionConfigurationBlock.Title));
            headerGrid.Add(titleLabel);

            var contentLabel = new Label
            {
                FontSize = 11,
                TextColor = Color.FromArgb("#999"),
                Margin = 10,
                BindingContext = block
            };
            contentLabel.SetBinding(Label.TextProperty, nameof(ReconstructionConfigurationBlock.HighlightedOption));

            mainStack.Add(headerGrid);
            mainStack.Add(contentLabel);
            border.Content = mainStack;

            var inPort = new Border
            {
                WidthRequest = 14,
                HeightRequest = 14,
                StrokeShape = new RoundRectangle { CornerRadius = 7 },
                BackgroundColor = Colors.IndianRed,
                Stroke = Colors.Black,
                StrokeThickness = 1,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(-7, 23, 0, 0)
            };

            var outPort = new Border
            {
                WidthRequest = 14,
                HeightRequest = 14,
                StrokeShape = new RoundRectangle { CornerRadius = 7 },
                BackgroundColor = Colors.LightGreen,
                Stroke = Colors.Black,
                StrokeThickness = 1,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(0, 23, -7, 0)
            };

            // Gestures
            var panGesture = new PanGestureRecognizer();
            panGesture.PanUpdated += (s, e) => OnBlockPanUpdated(block, border.Parent as View, e);
            border.GestureRecognizers.Add(panGesture);

            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (s, e) => _viewModel.SelectBlockCommand.Execute(block);
            border.GestureRecognizers.Add(tapGesture);

            var connectGesture = new PanGestureRecognizer();
            connectGesture.PanUpdated += (s, e) => OnConnectionPanUpdated(block, e);
            outPort.GestureRecognizers.Add(connectGesture);

            var container = new Grid { WidthRequest = 214, BindingContext = block };
            // Ensure individual blocks remain hit-testable even though the parent layout is transparent.
            container.InputTransparent = false;
            container.Add(border);
            container.Add(inPort);
            container.Add(outPort);

            AbsoluteLayout.SetLayoutBounds(container, new Rect(block.X, block.Y, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));

            // Bind IsSelected to trigger visual changes on block
            var trigger = new DataTrigger(typeof(Border))
            {
                Binding = new Binding("IsSelected", source: block),
                Value = true
            };
            trigger.Setters.Add(new Setter { Property = Border.StrokeProperty, Value = Color.FromArgb("#4CC9F0") });
            trigger.Setters.Add(new Setter { Property = Border.StrokeThicknessProperty, Value = 2 });
            border.Triggers.Add(trigger);

            return container;
        }

        private void OnBlockPanUpdated(ReconstructionConfigurationBlock block, View view, PanUpdatedEventArgs e)
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _dragStartPositions[block.Id] = new Point(block.X, block.Y);
                    break;
                case GestureStatus.Running:
                    if (_dragStartPositions.TryGetValue(block.Id, out Point start))
                    {
                        double newX = start.X + e.TotalX;
                        double newY = start.Y + e.TotalY;
                        block.X = newX;
                        block.Y = newY;
                        AbsoluteLayout.SetLayoutBounds(view, new Rect(newX, newY, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));
                        ConnectionsCanvas.InvalidateSurface();
                    }
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _dragStartPositions.Remove(block.Id);
                    break;
            }
        }

        private void OnConnectionPanUpdated(ReconstructionConfigurationBlock sourceBlock, PanUpdatedEventArgs e)
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    double startX = sourceBlock.X + OutputPortXOffset;
                    double startY = sourceBlock.Y + PortCenterY;
                    _tempConnectionSource = sourceBlock;
                    _tempConnectionStart = new Point(startX, startY);
                    _tempConnectionEnd = _tempConnectionStart;
                    ConnectionsCanvas.InvalidateSurface();
                    break;
                case GestureStatus.Running:
                    if (_tempConnectionStart.HasValue)
                    {
                        _tempConnectionEnd = new Point(_tempConnectionStart.Value.X + e.TotalX, _tempConnectionStart.Value.Y + e.TotalY);
                        ConnectionsCanvas.InvalidateSurface();
                    }
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    if (_tempConnectionEnd.HasValue)
                    {
                        var targetBlock = FindTargetBlock(_tempConnectionEnd.Value);
                        if (targetBlock != null && targetBlock != sourceBlock)
                        {
                            _viewModel.AddConnection(sourceBlock, targetBlock);
                        }
                    }
                    _tempConnectionSource = null;
                    _tempConnectionStart = null;
                    _tempConnectionEnd = null;
                    ConnectionsCanvas.InvalidateSurface();
                    break;
            }
        }

        private ReconstructionConfigurationBlock FindTargetBlock(Point location)
        {
            foreach (var block in _viewModel.Blocks)
            {
                double targetX = block.X + InputPortXOffset;
                double targetY = block.Y + PortCenterY;
                if (Math.Abs(location.X - targetX) < 40 && Math.Abs(location.Y - targetY) < 40)
                {
                    return block;
                }
            }
            return null;
        }

        // Combined Touch Handler for:
        // 1. Connection Selection (Click)
        // 2. Multi-Selection Rectangle (Drag)
        private void OnCanvasTouch(object sender, SKTouchEventArgs e)
        {
            // Coordinate conversion if needed, assuming Canvas scale is 1:1 with MAUI Points
            var viewPoint = ToViewPoint(e.Location);
            var viewSkPoint = new SKPoint((float)viewPoint.X, (float)viewPoint.Y);

            switch (e.ActionType)
            {
                case SKTouchAction.Pressed:
                    if (TrySelectConnection(viewSkPoint))
                    {
                        ResetInteractionState();
                        break;
                    }

                    if (TryBeginPortConnection(viewPoint))
                    {
                        break;
                    }

                    if (TryBeginBlockDrag(viewPoint))
                    {
                        break;
                    }

                    BeginSelection(viewPoint);
                    break;

                case SKTouchAction.Moved:
                    HandleMove(viewPoint);
                    break;

                case SKTouchAction.Released:
                case SKTouchAction.Cancelled:
                    CompleteInteraction(viewPoint);
                    break;
            }

            e.Handled = true;
        }

        private bool TryBeginPortConnection(Point viewPoint)
        {
            var block = FindBlockAtPoint(viewPoint);
            if (block == null)
            {
                return false;
            }

            var portCenter = new Point(block.X + OutputPortXOffset, block.Y + PortCenterY);
            if (Distance(viewPoint, portCenter) <= 16)
            {
                _tempConnectionSource = block;
                _tempConnectionStart = portCenter;
                _tempConnectionEnd = portCenter;
                ConnectionsCanvas.InvalidateSurface();
                return true;
            }

            return false;
        }

        private bool TryBeginBlockDrag(Point viewPoint)
        {
            var block = FindBlockAtPoint(viewPoint);
            if (block == null)
            {
                return false;
            }

            _draggedBlock = block;
            _dragStartPoint = viewPoint;
            _dragStartBlockPosition = new Point(block.X, block.Y);
            _viewModel.SelectBlock(block);
            return true;
        }

        private void BeginSelection(Point viewPoint)
        {
            _selectionStart = viewPoint;
            _isSelecting = true;
            _viewModel.ClearSelection();

            SelectionBox.IsVisible = true;
            SelectionBox.WidthRequest = 0;
            SelectionBox.HeightRequest = 0;
            SelectionBox.Margin = new Thickness(viewPoint.X, viewPoint.Y, 0, 0);
        }

        private void HandleMove(Point viewPoint)
        {
            if (_tempConnectionStart.HasValue && _tempConnectionSource != null)
            {
                _tempConnectionEnd = viewPoint;
                ConnectionsCanvas.InvalidateSurface();
                return;
            }

            if (_draggedBlock != null)
            {
                var delta = new Point(viewPoint.X - _dragStartPoint.X, viewPoint.Y - _dragStartPoint.Y);
                var newX = _dragStartBlockPosition.X + delta.X;
                var newY = _dragStartBlockPosition.Y + delta.Y;
                _draggedBlock.X = newX;
                _draggedBlock.Y = newY;
                var view = NodeContainer.Children
                    .OfType<View>()
                    .FirstOrDefault(c => ReferenceEquals(c.BindingContext, _draggedBlock));
                if (view != null)
                {
                    AbsoluteLayout.SetLayoutBounds(view, new Rect(newX, newY, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));
                }
                ConnectionsCanvas.InvalidateSurface();
                return;
            }

            if (_isSelecting && _selectionStart.HasValue)
            {
                double x = Math.Min(_selectionStart.Value.X, viewPoint.X);
                double y = Math.Min(_selectionStart.Value.Y, viewPoint.Y);
                double w = Math.Abs(_selectionStart.Value.X - viewPoint.X);
                double h = Math.Abs(_selectionStart.Value.Y - viewPoint.Y);

                SelectionBox.Margin = new Thickness(x, y, 0, 0);
                SelectionBox.WidthRequest = w;
                SelectionBox.HeightRequest = h;

                _viewModel.UpdateSelection(new Rect(x, y, w, h));
            }
        }

        private void CompleteInteraction(Point viewPoint)
        {
            if (_tempConnectionStart.HasValue && _tempConnectionSource != null)
            {
                var targetBlock = FindTargetBlock(viewPoint);
                if (targetBlock != null && targetBlock != _tempConnectionSource)
                {
                    _viewModel.AddConnection(_tempConnectionSource, targetBlock);
                }
                ResetInteractionState();
                return;
            }

            if (_draggedBlock != null)
            {
                _draggedBlock = null;
                return;
            }

            if (_isSelecting)
            {
                SelectionBox.IsVisible = false;
                _isSelecting = false;
                _selectionStart = null;
            }
        }

        private void ResetInteractionState()
        {
            _tempConnectionSource = null;
            _tempConnectionStart = null;
            _tempConnectionEnd = null;
            _draggedBlock = null;
            _isSelecting = false;
            _selectionStart = null;
            SelectionBox.IsVisible = false;
        }

        private ReconstructionConfigurationBlock? FindBlockAtPoint(Point point)
        {
            return _viewModel.Blocks.FirstOrDefault(block =>
                point.X >= block.X && point.X <= block.X + BlockWidth &&
                point.Y >= block.Y && point.Y <= block.Y + BlockHeight);
        }

        private static double Distance(Point a, Point b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private bool TrySelectConnection(SKPoint clickPoint)
        {
            ReconstructionConnection bestMatch = null;
            double minDistance = 20.0;

            foreach (var conn in _viewModel.Connections)
            {
                if (conn.Source == null || conn.Target == null) continue;

                float x1 = (float)(conn.Source.X + OutputPortXOffset);
                float y1 = (float)(conn.Source.Y + PortCenterY);
                float x2 = (float)(conn.Target.X + InputPortXOffset);
                float y2 = (float)(conn.Target.Y + PortCenterY);

                float midX = (x1 + x2) / 2;
                float midY = (y1 + y2) / 2;

                double dist = Math.Sqrt(Math.Pow(clickPoint.X - midX, 2) + Math.Pow(clickPoint.Y - midY, 2));
                if (dist < minDistance)
                {
                    minDistance = dist;
                    bestMatch = conn;
                }
            }

            if (bestMatch != null)
            {
                _viewModel.SelectConnection(bestMatch);
                ConnectionsCanvas.InvalidateSurface();
                return true;
            }
            return false;
        }

        private void OnCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear();

            using var paintNormal = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColor.Parse("#F72585"),
                StrokeWidth = 3,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };

            using var paintSelected = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Orange,
                StrokeWidth = 5,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };

            foreach (var conn in _viewModel.Connections)
            {
                if (conn.Source == null || conn.Target == null) continue;

                float x1 = (float)(conn.Source.X + OutputPortXOffset);
                float y1 = (float)(conn.Source.Y + PortCenterY);
                float x2 = (float)(conn.Target.X + InputPortXOffset);
                float y2 = (float)(conn.Target.Y + PortCenterY);

                DrawConnectionCurve(canvas, conn.IsSelected ? paintSelected : paintNormal, x1, y1, x2, y2);
            }

            if (_tempConnectionStart.HasValue && _tempConnectionEnd.HasValue)
            {
                using var tempPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = SKColors.White,
                    StrokeWidth = 3,
                    IsAntialias = true,
                    StrokeCap = SKStrokeCap.Round,
                    PathEffect = SKPathEffect.CreateDash(new float[] { 10, 10 }, 0)
                };

                float x1 = (float)_tempConnectionStart.Value.X;
                float y1 = (float)_tempConnectionStart.Value.Y;
                float x2 = (float)_tempConnectionEnd.Value.X;
                float y2 = (float)_tempConnectionEnd.Value.Y;

                DrawConnectionCurve(canvas, tempPaint, x1, y1, x2, y2);
            }
        }

        private Point ToViewPoint(SKPoint skPoint)
        {
            var canvasSize = ConnectionsCanvas.CanvasSize;
            var viewWidth = ConnectionsCanvas.Width;
            var viewHeight = ConnectionsCanvas.Height;

            if (canvasSize.Width <= 0 || canvasSize.Height <= 0 || viewWidth <= 0 || viewHeight <= 0)
            {
                return new Point(skPoint.X, skPoint.Y);
            }

            double x = skPoint.X * viewWidth / canvasSize.Width;
            double y = skPoint.Y * viewHeight / canvasSize.Height;
            return new Point(x, y);
        }

        private void DrawConnectionCurve(SKCanvas canvas, SKPaint paint, float x1, float y1, float x2, float y2)
        {
            using var path = new SKPath();
            path.MoveTo(x1, y1);
            float cp1X = x1 + 60;
            float cp2X = x2 - 60;
            path.CubicTo(cp1X, y1, cp2X, y2, x2, y2);
            canvas.DrawPath(path, paint);
        }

        private void OnMeshPreviewPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Black.WithAlpha(20));

            var discretization = Workspace.GetDiscretization();
            MeshPreviewPlaceholder.IsVisible = discretization == null;
            if (discretization == null)
                return;

            if (discretization is LBMGrid lbm)
            {
                DrawLbmPreview(canvas, info, lbm);
            }
            else if (discretization is FEMMesh fem)
            {
                DrawFemPreview(canvas, info, fem);
            }
        }

        private void DrawLbmPreview(SKCanvas canvas, SKImageInfo info, LBMGrid grid)
        {
            float cellW = (float)info.Width / grid.Nx;
            float cellH = (float)info.Height / grid.Ny;

            for (int y = 0; y < grid.Ny; y++)
            {
                for (int x = 0; x < grid.Nx; x++)
                {
                    var el = grid.GetElementAt(x, y);
                    SKPaint fill = el.IsElectrode
                        ? _lbmElectrode
                        : el.IsWall
                            ? _lbmWall
                            : _lbmFill;
                    var r = SKRect.Create(x * cellW, y * cellH, cellW, cellH);
                    canvas.DrawRect(r, fill);
                    canvas.DrawRect(r, _lbmStroke);
                }
            }
        }

        private static SKColor ColorForValue(double val, double min, double max)
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

        private void DrawFemPreview(SKCanvas canvas, SKImageInfo info, FEMMesh mesh)
        {
            const float pad = 6f;
            float availW = info.Width - 2 * pad;
            float availH = info.Height - 2 * pad;
            var verts = mesh.Vertices;
            float minX = (float)verts.Min(v => v.X);
            float minY = (float)verts.Min(v => v.Y);
            var maxX = (float)verts.Max(v => v.X);
            var maxY = (float)verts.Max(v => v.Y);
            float meshWidth = maxX - minX;
            float meshHeight = maxY - minY;
            float scale = Math.Min(availW / meshWidth, availH / meshHeight);
            float usedW = meshWidth * scale;
            float usedH = meshHeight * scale;
            float marginX = pad + (availW - usedW) / 2f;
            float marginY = pad + (availH - usedH) / 2f;

            SKPoint ToCanvas(Utility.Classes.Discretizer.FiniteElementMesh.FEMVertex v)
                => new((float)(v.X - minX) * scale + marginX,
                        info.Height - ((float)(v.Y - minY) * scale + marginY));

            var elements = mesh.ElementsTyped;
            double min = elements.Min(el => el.Conductivity);
            double max = elements.Max(el => el.Conductivity);

            using var path = new SKPath();
            foreach (var el in elements)
            {
                var p1 = ToCanvas(el.Vertices[0]);
                var p2 = ToCanvas(el.Vertices[1]);
                var p3 = ToCanvas(el.Vertices[2]);
                path.Reset();
                path.MoveTo(p1); path.LineTo(p2); path.LineTo(p3); path.Close();
                _femFill.Color = ColorForValue(el.Conductivity, min, max);
                canvas.DrawPath(path, _femFill);
                canvas.DrawPath(path, _femStroke);
            }

            foreach (var segment in mesh.GetElectrodeSegments())
            {
                var start = ToCanvas(segment.Start);
                var end = ToCanvas(segment.End);
                canvas.DrawLine(start, end, _electrodeSegmentStroke);
            }

            foreach (var v in mesh.Vertices.Where(v => v.IsElectrode))
                canvas.DrawCircle(ToCanvas(v), 3f, _electrodeFill);
        }

        private void OnAddBlockClicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is BlockType type)
            {
                _viewModel.AddBlock(type, 100, 100);
            }
        }
    }
}