using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using System.Collections.Specialized;
using Utility.Classes.Configurations.ReconstructionConfiguration;

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

        // Selection Rectangle State
        private Point? _selectionStart;
        private bool _isSelecting = false;

        private const double PortCenterY = 30;
        private const double OutputPortXOffset = 214;
        private const double InputPortXOffset = 0;

        public ReconstructionConfigurationPage()
        {
            InitializeComponent();
            _viewModel = new ReconstructionConfigurationPageViewModel();
            BindingContext = _viewModel;

            _viewModel.Blocks.CollectionChanged += OnBlocksChanged;
            _viewModel.Connections.CollectionChanged += (s, e) => ConnectionsCanvas.InvalidateSurface();

            RefreshNodeContainer();
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
            headerGrid.Add(new Label
            {
                Text = block.Title,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#F0F0F0"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });

            var contentLabel = new Label { Text = $"{block.Type}", FontSize = 11, TextColor = Color.FromArgb("#999"), Margin = 10 };

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

            var container = new Grid { WidthRequest = 214 };
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
            var pt = e.Location;

            switch (e.ActionType)
            {
                case SKTouchAction.Pressed:
                    // Try selecting a connection first
                    if (TrySelectConnection(pt))
                    {
                        _isSelecting = false;
                    }
                    else
                    {
                        // Start Selection Rectangle
                        _selectionStart = new Point(pt.X, pt.Y);
                        _isSelecting = true;
                        _viewModel.ClearSelection();

                        SelectionBox.IsVisible = true;
                        SelectionBox.WidthRequest = 0;
                        SelectionBox.HeightRequest = 0;
                        SelectionBox.Margin = new Thickness(pt.X, pt.Y, 0, 0);
                    }
                    break;

                case SKTouchAction.Moved:
                    if (_isSelecting && _selectionStart.HasValue)
                    {
                        double x = Math.Min(_selectionStart.Value.X, pt.X);
                        double y = Math.Min(_selectionStart.Value.Y, pt.Y);
                        double w = Math.Abs(_selectionStart.Value.X - pt.X);
                        double h = Math.Abs(_selectionStart.Value.Y - pt.Y);

                        // Update Visual Rect
                        SelectionBox.Margin = new Thickness(x, y, 0, 0);
                        SelectionBox.WidthRequest = w;
                        SelectionBox.HeightRequest = h;

                        // Real-time selection updates
                        _viewModel.UpdateSelection(new Rect(x, y, w, h));
                    }
                    break;

                case SKTouchAction.Released:
                case SKTouchAction.Cancelled:
                    if (_isSelecting)
                    {
                        SelectionBox.IsVisible = false;
                        _isSelecting = false;
                        _selectionStart = null;
                    }
                    break;
            }

            e.Handled = true;
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

        private void DrawConnectionCurve(SKCanvas canvas, SKPaint paint, float x1, float y1, float x2, float y2)
        {
            using var path = new SKPath();
            path.MoveTo(x1, y1);
            float cp1X = x1 + 60;
            float cp2X = x2 - 60;
            path.CubicTo(cp1X, y1, cp2X, y2, x2, y2);
            canvas.DrawPath(path, paint);
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