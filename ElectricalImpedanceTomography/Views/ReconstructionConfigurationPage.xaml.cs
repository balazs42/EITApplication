using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Utility.Classes.Application;
using Utility.Classes.Configurations.ReconstructionConfiguration;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Configurations.ReconstructionConfiguration.Rules;

namespace ElectricalImpedanceTomography.Views
{
    public partial class ReconstructionConfigurationPage : ContentPage
    {
        // ViewModel backing this page
        private readonly ReconstructionConfigurationPageViewModel _viewModel;

        // Tracks initial positions of blocks at the start of a drag (for multi-move)
        private readonly Dictionary<ReconstructionConfigurationBlock, Point> _dragStartPositions = new();

        // Temporary connection state while the user drags from an output port
        private Point? _tempConnectionStart;
        private Point? _tempConnectionEnd;
        private ReconstructionConfigurationBlock? _tempConnectionSource;

        // Selection rectangle state when dragging on empty canvas
        private Point? _selectionStart;
        private bool _isSelecting = false;

        // Dragging state for block moves
        private ReconstructionConfigurationBlock? _draggedBlock;
        private Point _dragStartPoint;

        // Canvas panning state for right-click drag
        private bool _isCanvasPanning;
        private Point _canvasPanStart;
        private Point _canvasScrollStart;

        // LBM preview drawing brushes
        private readonly SKPaint _lbmFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black };
        private readonly SKPaint _lbmWall = new() { Style = SKPaintStyle.Fill, Color = SKColors.White };
        private readonly SKPaint _lbmElectrode = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange };
        private readonly SKPaint _lbmStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };

        // FEM preview drawing brushes
        private readonly SKPaint _femStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };
        private readonly SKPaint _femFill = new() { Style = SKPaintStyle.Fill };
        private readonly SKPaint _electrodeFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Yellow };
        private readonly SKPaint _electrodeSegmentStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Gold, StrokeWidth = 3, IsAntialias = true };

        // Enlarged hit-targets for connection interactions to make drawing easier
        private const double OutputPortHitRadius = 40;   // was 16
        private const double TargetPortHitHalfSize = 70; // was 40
        private const double MinBlockWidth = 140;
        private const double MinBlockHeight = 60;
        private const double MaxBlockWidth = 520;
        private const double MaxBlockHeight = 360;

        private const double PortMarginTop = 22;
        private const double PortSize = 22;

        private readonly HashSet<ReconstructionConfigurationBlock> _subscribedBlocks = new();

        private double _canvasScale = 1.0;
        private const double MinCanvasScale = 0.5;
        private const double MaxCanvasScale = 2.5;

        public ReconstructionConfigurationPage()
        {
            InitializeComponent();

            // Create and attach the ViewModel
            _viewModel = new ReconstructionConfigurationPageViewModel();
            BindingContext = _viewModel;

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Rebuild the node visuals whenever blocks change
            _viewModel.Blocks.CollectionChanged += OnBlocksChanged;

            // Redraw connections whenever the connection collection changes
            _viewModel.Connections.CollectionChanged += (s, e) => ConnectionsCanvas.InvalidateSurface();

            // Initial layout of blocks
            RefreshNodeContainer();
            ApplyCanvasScale();
            SnapBlocksToGrid();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            // Ensure mesh preview is up-to-date when the page becomes visible
            MeshPreviewCanvas?.InvalidateSurface();
        }

        private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var old in e.OldItems.OfType<ReconstructionConfigurationBlock>())
                {
                    if (_subscribedBlocks.Remove(old))
                    {
                        old.PropertyChanged -= OnBlockPropertyChanged;
                    }
                }
            }

            // Re-create visual containers for blocks after any add/remove
            RefreshNodeContainer();
            SnapBlocksToGrid();
            // Connections depend on block positions/sizes; redraw
            ConnectionsCanvas.InvalidateSurface();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReconstructionConfigurationPageViewModel.GridSpacing))
            {
                SnapBlocksToGrid();
                GridCanvas.InvalidateSurface();
                ConnectionsCanvas.InvalidateSurface();
            }
            else if (e.PropertyName == nameof(ReconstructionConfigurationPageViewModel.CanvasWidth)
                     || e.PropertyName == nameof(ReconstructionConfigurationPageViewModel.CanvasHeight))
            {
                ApplyCanvasScale();
            }
        }

        private void RefreshNodeContainer()
        {
            // Clear and re-add all block visuals (simpler than diffing)
            NodeContainer.Children.Clear();
            foreach (var block in _viewModel.Blocks)
            {
                EnsureBlockSubscription(block);
                var view = CreateBlockView(block);
                NodeContainer.Children.Add(view);
            }
        }

        private void EnsureBlockSubscription(ReconstructionConfigurationBlock block)
        {
            if (_subscribedBlocks.Contains(block))
            {
                return;
            }

            _subscribedBlocks.Add(block);
            block.PropertyChanged += OnBlockPropertyChanged;
            EnforceBlockSize(block);
        }

        private View CreateBlockView(ReconstructionConfigurationBlock block)
        {
            // Visual card for a block (rounded border with header and content)
            var border = new Border
            {
                Stroke = Color.FromArgb("#555"),
                StrokeThickness = 1,
                BackgroundColor = Color.FromArgb("#3A3A4E"),
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                WidthRequest = 200,
                HeightRequest = 80,
                Shadow = new Shadow { Brush = Brush.Black, Offset = new Point(0, 5), Opacity = 0.4f, Radius = 10 }
            };

            // Bind visual size to block model properties
            border.SetBinding(WidthRequestProperty, new Binding(nameof(ReconstructionConfigurationBlock.Width), source: block));
            border.SetBinding(HeightRequestProperty, new Binding(nameof(ReconstructionConfigurationBlock.Height), source: block));

            var headerColor = Color.FromArgb(block.IconColor);

            // Build header with colored strip and title
            var mainStack = new VerticalStackLayout();
            var headerGrid = new Grid { BackgroundColor = headerColor.WithAlpha(0.15f), Padding = 10, HeightRequest = 40 };
            headerGrid.Add(new BoxView { Color = headerColor, WidthRequest = 4, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Fill });
            var titleLabel = new Label
            {
                FontSize = block.FontSize,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#F0F0F0"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(10, 0, 0, 0),
                BindingContext = block
            };
            titleLabel.SetBinding(Label.TextProperty, nameof(ReconstructionConfigurationBlock.Title));
            titleLabel.SetBinding(Label.FontSizeProperty, new Binding(nameof(ReconstructionConfigurationBlock.FontSize), source: block));
            headerGrid.Add(titleLabel);

            // Secondary info text (e.g., highlighted option)
            var contentLabel = new Label
            {
                FontSize = block.FontSize,
                TextColor = Color.FromArgb("#999"),
                Margin = 10,
                BindingContext = block
            };
            contentLabel.SetBinding(Label.TextProperty, nameof(ReconstructionConfigurationBlock.HighlightedOption));
            contentLabel.SetBinding(Label.FontSizeProperty, new Binding(nameof(ReconstructionConfigurationBlock.FontSize), source: block));

            mainStack.Add(headerGrid);
            mainStack.Add(contentLabel);
            border.Content = mainStack;

            // Input port bubble (left side). Visible only if the block accepts inputs per rules
            var inPort = new Border
            {
                WidthRequest = 22,
                HeightRequest = 22,
                StrokeShape = new RoundRectangle { CornerRadius = 7 },
                BackgroundColor = Colors.IndianRed,
                Stroke = Colors.Black,
                StrokeThickness = 1,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(-11, 22, 0, 0),
                IsVisible = ReconstructionConfigurationRules.GetConnectionConstraint(block.Type).MaxInputs > 0
            };
            //inPort.Cursor = Cursor.Cross;

            // Output port bubble (right side)
            var outPort = new Border
            {
                WidthRequest = 22,
                HeightRequest = 22,
                StrokeShape = new RoundRectangle { CornerRadius = 7 },
                BackgroundColor = Colors.LightGreen,
                Stroke = Colors.Black,
                StrokeThickness = 1,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(0, 22, -11, 0)
            };
            //outPort.Cursor = Cursor.Cross;

            // Gestures
            // 1) Pan on the border moves the selected block(s) with the primary button
            var panGesture = new PanGestureRecognizer
            {
                Buttons = Microsoft.Maui.Controls.ButtonsMask.Primary
            };
            panGesture.PanUpdated += (s, e) => OnBlockPanUpdated(block, border.Parent as View, e);
            border.GestureRecognizers.Add(panGesture);

            // 2) Primary click/tap selects the block (no rotation)
            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (s, e) =>
            {
                _viewModel.SelectBlockCommand.Execute(block);
            };
            // Only respond to primary pointer (left mouse button / normal tap)
            tapGesture.Buttons = Microsoft.Maui.Controls.ButtonsMask.Primary;
            border.GestureRecognizers.Add(tapGesture);

            // 3) Secondary pan (right click + drag) begins a connection from the block's output port
            var connectGesture = new PanGestureRecognizer
            {
                Buttons = Microsoft.Maui.Controls.ButtonsMask.Secondary
            };
            connectGesture.PanUpdated += (s, e) => OnConnectionPanUpdated(block, e);
            border.GestureRecognizers.Add(connectGesture);

            // 4) Double primary tap either arms resize (bottom-right corner) or opens the initialization editor
            var doubleTapGesture = new TapGestureRecognizer
            {
                NumberOfTapsRequired = 2,
                Buttons = Microsoft.Maui.Controls.ButtonsMask.Primary
            };
            doubleTapGesture.Tapped += async (s, e) => await OnBlockDoubleTappedAsync(block, e, s as View);
            border.GestureRecognizers.Add(doubleTapGesture);

            // 5) Double secondary click rotates the block and selects it
            var doubleRightClickRotateGesture = new TapGestureRecognizer
            {
                NumberOfTapsRequired = 2,
                Buttons = Microsoft.Maui.Controls.ButtonsMask.Secondary
            };
            doubleRightClickRotateGesture.Tapped += (s, e) =>
            {
                _viewModel.SelectBlockCommand.Execute(block);
                _viewModel.RotateBlockCommand.Execute(block);
            };
            border.GestureRecognizers.Add(doubleRightClickRotateGesture);

            // Container that hosts the card and port bubbles; also bound to rotation
            var container = new Grid { WidthRequest = 214, HeightRequest = 80, BindingContext = block };
            container.SetBinding(WidthRequestProperty, new Binding(nameof(ReconstructionConfigurationBlock.Width), source: block));
            container.SetBinding(HeightRequestProperty, new Binding(nameof(ReconstructionConfigurationBlock.Height), source: block));
            container.SetBinding(RotationProperty, new Binding(nameof(ReconstructionConfigurationBlock.Rotation), source: block));
            container.AnchorX = 0;
            container.AnchorY = 0;
            // Keep hit-testing enabled for the child visuals
            container.InputTransparent = false;
            container.Add(border);
            container.Add(inPort);
            container.Add(outPort);

            // Initial placement in the absolute layout
            ApplyBlockLayout(block, container);

            // Visual selection cue: thicker cyan border when block.IsSelected is true
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

        private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ReconstructionConfigurationBlock block)
            {
                return;
            }

            if (e.PropertyName == nameof(ReconstructionConfigurationBlock.Width) ||
                e.PropertyName == nameof(ReconstructionConfigurationBlock.Height))
            {
                EnforceBlockSize(block);
                UpdateBlockViewLayout(block);
                ConnectionsCanvas.InvalidateSurface();
                _viewModel.NotifyLayoutChanged();
            }
            else if (e.PropertyName == nameof(ReconstructionConfigurationBlock.X) ||
                     e.PropertyName == nameof(ReconstructionConfigurationBlock.Y))
            {
                UpdateBlockViewLayout(block);
                ConnectionsCanvas.InvalidateSurface();
            }
        }

        /// <summary>
        /// Handles double-tap on a block card. If the block is the Initialization block,
        /// opens a popup to edit the initial conductivity distribution.
        /// </summary>
        private async Task OnBlockDoubleTappedAsync(ReconstructionConfigurationBlock block, TappedEventArgs tapArgs, View? sourceView)
        {
            var position = sourceView != null ? tapArgs.GetPosition(sourceView) : null;
            // Only Initialization blocks support this editor
            if (block.Type != BlockType.Initialization)
                return;

            // You must have a discretization/mesh to preview/edit
            var discretization = Workspace.GetDiscretization();
            if (discretization == null)
            {
                await DisplayAlert("No Mesh", "You should create or load a mesh before editing the initial distribution!", "Ok");
                return;
            }

            // Use current distribution if present, otherwise take a copy from mesh
            var initial = Workspace.GetInitialConductivityDistribution() ?? discretization.GetConductivityDistribution();
            var original = Workspace.GetOriginalConductivityDistribution();
            var parameters = Workspace.GetReconstructionParameters();

            // Create and show popup; update preview while changes are made
            var popup = new InitialDistributionEditorPopup(discretization,
                                                           initial,
                                                           original,
                                                           parameters.InitialDistributionType);

            EventHandler handler = (_, _) => MeshPreviewCanvas?.InvalidateSurface();
            popup.DistributionChanged += handler;
            await this.ShowPopupAsync(popup);
            popup.DistributionChanged -= handler;
        }

        /// <summary>
        /// Pan gesture on the block body. Moves all selected blocks as a group.
        /// </summary>
        private void OnBlockPanUpdated(ReconstructionConfigurationBlock block, View view, PanUpdatedEventArgs e)
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    // Capture starting positions for selected blocks
                    PrepareDragPositions(block);
                    break;
                case GestureStatus.Running:
                    // Apply delta to every moved block
                    foreach (var kvp in _dragStartPositions)
                    {
                        var start = kvp.Value;
                        var newX = start.X + e.TotalX / _canvasScale;
                        var newY = start.Y + e.TotalY / _canvasScale;
                        UpdateBlockPosition(kvp.Key, newX, newY);
                    }
                    // Redraw connection curves
                    ConnectionsCanvas.InvalidateSurface();
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    // Clear temp state and notify ViewModel so it can update workspace/state
                    _dragStartPositions.Clear();
                    _viewModel.NotifyLayoutChanged();
                    break;
            }
        }

        /// <summary>
        /// Initializes the map of blocks to their starting positions at the beginning of a drag.
        /// If no block is selected, only the current block moves.
        /// </summary>
        private void PrepareDragPositions(ReconstructionConfigurationBlock block)
        {
            _dragStartPositions.Clear();
            var selected = _viewModel.Blocks.Where(b => b.IsSelected).ToList();
            if (!selected.Any())
            {
                selected.Add(block);
            }

            foreach (var b in selected.Distinct())
            {
                _dragStartPositions[b] = new Point(b.X, b.Y);
            }
        }

        /// <summary>
        /// Writes new X/Y to the block model and updates its visual bounds in the absolute layout.
        /// </summary>
        private void UpdateBlockPosition(ReconstructionConfigurationBlock block, double newX, double newY)
        {
            var snappedX = SnapToGrid(newX);
            var snappedY = SnapToGrid(newY);

            block.X = snappedX;
            block.Y = snappedY;
            var view = NodeContainer.Children
                .OfType<View>()
                .FirstOrDefault(c => ReferenceEquals(c.BindingContext, block));
            if (view != null)
            {
                ApplyBlockLayout(block, view);
            }
        }

        private double SnapToGrid(double value)
        {
            var spacing = _viewModel.GridSpacing;
            if (spacing <= 0)
            {
                return value;
            }

            return Math.Round(value / spacing) * spacing;
        }

        private void SnapBlocksToGrid()
        {
            foreach (var block in _viewModel.Blocks)
            {
                block.X = SnapToGrid(block.X);
                block.Y = SnapToGrid(block.Y);
                EnforceBlockSize(block);

                UpdateBlockViewLayout(block);
            }

            ConnectionsCanvas.InvalidateSurface();
            GridCanvas.InvalidateSurface();
            _viewModel.NotifyLayoutChanged();
        }

        private void EnforceBlockSize(ReconstructionConfigurationBlock block)
        {
            block.Width = Math.Clamp(block.Width, MinBlockWidth, MaxBlockWidth);
            block.Height = Math.Clamp(block.Height, MinBlockHeight, MaxBlockHeight);
        }

        private void UpdateBlockViewLayout(ReconstructionConfigurationBlock block)
        {
            var view = NodeContainer.Children
                .OfType<View>()
                .FirstOrDefault(c => ReferenceEquals(c.BindingContext, block));

            if (view != null)
            {
                ApplyBlockLayout(block, view);
            }
        }

        private void ApplyBlockLayout(ReconstructionConfigurationBlock block, View view)
        {
            view.Scale = _canvasScale;
            AbsoluteLayout.SetLayoutBounds(view, new Rect(block.X * _canvasScale, block.Y * _canvasScale, block.Width, block.Height));
        }

        private void ApplyCanvasScale()
        {
            var width = _viewModel.CanvasWidth * _canvasScale;
            var height = _viewModel.CanvasHeight * _canvasScale;

            CanvasWrapper.WidthRequest = width;
            CanvasWrapper.HeightRequest = height;
            GridCanvas.WidthRequest = width;
            GridCanvas.HeightRequest = height;
            ConnectionsCanvas.WidthRequest = width;
            ConnectionsCanvas.HeightRequest = height;
            NodeContainer.WidthRequest = width;
            NodeContainer.HeightRequest = height;

            foreach (var child in NodeContainer.Children.OfType<View>())
            {
                if (child.BindingContext is ReconstructionConfigurationBlock block)
                {
                    ApplyBlockLayout(block, child);
                }
            }

            GridCanvas.InvalidateSurface();
            ConnectionsCanvas.InvalidateSurface();
        }

        private Point ScalePoint(Point logical) => new(logical.X * _canvasScale, logical.Y * _canvasScale);

        /// <summary>
        /// Tracks the creation of a new connection while the user drags from an output port.
        /// </summary>
        private void OnConnectionPanUpdated(ReconstructionConfigurationBlock sourceBlock, PanUpdatedEventArgs e)
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    // Verify the block can still output (capacity rules)
                    if (!_viewModel.HasOutputCapacity(sourceBlock))
                    {
                        return;
                    }
                    var anchor = GetOutputPortAnchor(sourceBlock);
                    _tempConnectionSource = sourceBlock;
                    _tempConnectionStart = anchor;
                    _tempConnectionEnd = anchor;
                    ConnectionsCanvas.InvalidateSurface();
                    break;
                case GestureStatus.Running:
                    // Update the temporary end point relative to drag delta
                    if (_tempConnectionStart.HasValue)
                    {
                        _tempConnectionEnd = new Point(_tempConnectionStart.Value.X + e.TotalX / _canvasScale, _tempConnectionStart.Value.Y + e.TotalY / _canvasScale);
                        ConnectionsCanvas.InvalidateSurface();
                    }
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    // On release, try to find a valid target and commit the connection
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

        /// <summary>
        /// Finds a target block whose input port lies near the provided location.
        /// Uses a proximity threshold around the input anchor.
        /// </summary>
        private ReconstructionConfigurationBlock FindTargetBlock(Point location)
        {
            foreach (var block in _viewModel.Blocks)
            {
                if (!_viewModel.HasInputCapacity(block))
                {
                    continue;
                }

                var anchor = GetInputPortAnchor(block);
                // Expanded target area: square region around input port center
                if (Math.Abs(location.X - anchor.X) < TargetPortHitHalfSize && Math.Abs(location.Y - anchor.Y) < TargetPortHitHalfSize)
                {
                    return block;
                }
            }
            return null;
        }

        // Combined Touch Handler on the connection canvas for:
        // 1) Selecting a connection by clicking near its midpoint
        // 2) Drawing a selection rectangle on empty space
        // 3) Delegating to connection-drag or block-drag initiation when appropriate
        private void OnCanvasTouch(object sender, SKTouchEventArgs e)
        {
            if (e.MouseButton == SKMouseButton.Right)
            {
                var viewPoint = ToViewPoint(e.Location);
                switch (e.ActionType)
                {
                    case SKTouchAction.Pressed:
                        BeginCanvasPan(viewPoint);
                        break;
                    case SKTouchAction.Moved:
                        UpdateCanvasPan(viewPoint);
                        break;
                    case SKTouchAction.Released:
                    case SKTouchAction.Cancelled:
                        EndCanvasPan();
                        break;
                }

                e.Handled = true;
                return;
            }

            // Convert Skia coordinates into MAUI view coordinates to compare with block bounds
            var logicalPoint = ToLogicalPoint(e.Location);
            var viewSkPoint = new SKPoint((float)(logicalPoint.X * _canvasScale), (float)(logicalPoint.Y * _canvasScale));

            switch (e.ActionType)
            {
                case SKTouchAction.Pressed:
                    // Prefer selecting connections if the press is near a connection midpoint
                    if (TrySelectConnection(viewSkPoint))
                    {
                        ResetInteractionState();
                        break;
                    }

                    // If press is near an output, begin port connection creation
                    if (TryBeginPortConnection(logicalPoint))
                    {
                        break;
                    }

                    // If press is over a block, begin a drag of the block (or selected group)
                    if (TryBeginBlockDrag(logicalPoint))
                    {
                        break;
                    }

                    // Otherwise, start selection rectangle on empty canvas
                    BeginSelection(logicalPoint);
                    break;

                case SKTouchAction.Moved:
                    HandleMove(logicalPoint);
                    break;

                case SKTouchAction.Released:
                case SKTouchAction.Cancelled:
                    CompleteInteraction(logicalPoint);
                    break;
            }

            e.Handled = true;
        }

        /// <summary>
        /// If the pointer is on an output port (and capacity allows), start a temp connection drag.
        /// </summary>
        private bool TryBeginPortConnection(Point viewPoint)
        {
            var block = FindBlockAtPoint(viewPoint);
            if (block == null)
            {
                return false;
            }

            if (!_viewModel.HasOutputCapacity(block))
            {
                return false;
            }

            var portCenter = GetOutputPortAnchor(block);
            // Enlarged hit radius to make starting a connection easier
            if (Distance(viewPoint, portCenter) <= OutputPortHitRadius)
            {
                _tempConnectionSource = block;
                _tempConnectionStart = portCenter;
                _tempConnectionEnd = portCenter;
                ConnectionsCanvas.InvalidateSurface();
                return true;
            }

            return false;
        }

        /// <summary>
        /// If the pointer is over a block, begin dragging it (and any currently selected blocks).
        /// </summary>
        private bool TryBeginBlockDrag(Point viewPoint)
        {
            var block = FindBlockAtPoint(viewPoint);
            if (block == null)
            {
                return false;
            }

            _draggedBlock = block;
            _dragStartPoint = viewPoint;

            // Prepare group dragging; if nothing selected, select the block being dragged
            _dragStartPositions.Clear();
            var blocksToMove = _viewModel.Blocks.Where(b => b.IsSelected).ToList();
            if (!blocksToMove.Any())
            {
                _viewModel.SelectBlock(block);
                blocksToMove.Add(block);
            }

            foreach (var b in blocksToMove.Distinct())
            {
                _dragStartPositions[b] = new Point(b.X, b.Y);
            }

            return true;
        }

        /// <summary>
        /// Initializes the selection rectangle and clears current selection.
        /// </summary>
        private void BeginSelection(Point viewPoint)
        {
            _selectionStart = viewPoint;
            _isSelecting = true;
            _viewModel.ClearSelection();

            // Initialize the selection box overlay
            SelectionBox.IsVisible = true;
            SelectionBox.WidthRequest = 0;
            SelectionBox.HeightRequest = 0;
            SelectionBox.Margin = new Thickness(viewPoint.X * _canvasScale, viewPoint.Y * _canvasScale, 0, 0);
        }

        /// <summary>
        /// Moves either the temporary connection end, the dragged blocks, or updates selection rectangle.
        /// </summary>
        private void HandleMove(Point viewPoint)
        {
            // Dragging a connection: update its end point and redraw
            if (_tempConnectionStart.HasValue && _tempConnectionSource != null)
            {
                _tempConnectionEnd = viewPoint;
                ConnectionsCanvas.InvalidateSurface();
                return;
            }

            // Dragging block(s): apply delta to all and redraw
            if (_draggedBlock != null)
            {
                var delta = new Point(viewPoint.X - _dragStartPoint.X, viewPoint.Y - _dragStartPoint.Y);

                foreach (var kvp in _dragStartPositions)
                {
                    var newX = kvp.Value.X + delta.X;
                    var newY = kvp.Value.Y + delta.Y;
                    UpdateBlockPosition(kvp.Key, newX, newY);
                }

                ConnectionsCanvas.InvalidateSurface();
                return;
            }

            // Updating selection rectangle overlay and selection in ViewModel
            if (_isSelecting && _selectionStart.HasValue)
            {
                double x = Math.Min(_selectionStart.Value.X, viewPoint.X);
                double y = Math.Min(_selectionStart.Value.Y, viewPoint.Y);
                double w = Math.Abs(_selectionStart.Value.X - viewPoint.X);
                double h = Math.Abs(_selectionStart.Value.Y - viewPoint.Y);

                SelectionBox.Margin = new Thickness(x * _canvasScale, y * _canvasScale, 0, 0);
                SelectionBox.WidthRequest = w * _canvasScale;
                SelectionBox.HeightRequest = h * _canvasScale;

                _viewModel.UpdateSelection(new Rect(x, y, w, h));
            }
        }

        /// <summary>
        /// Finalizes the current user interaction: commits connection, finishes drag, or hides selection box.
        /// </summary>
        private void CompleteInteraction(Point viewPoint)
        {
            // If finishing a connection, try to attach to a nearby input port
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

            // Finish a block drag and notify VM
            if (_draggedBlock != null)
            {
                _draggedBlock = null;
                _dragStartPositions.Clear();
                _viewModel.NotifyLayoutChanged();
                return;
            }

            // End selection mode and hide overlay
            if (_isSelecting)
            {
                SelectionBox.IsVisible = false;
                _isSelecting = false;
                _selectionStart = null;
            }
        }

        /// <summary>
        /// Resets any in-progress interaction (connection/drag/selection) and hides selection overlay.
        /// </summary>
        private void ResetInteractionState()
        {
            _tempConnectionSource = null;
            _tempConnectionStart = null;
            _tempConnectionEnd = null;
            _draggedBlock = null;
            _isSelecting = false;
            _selectionStart = null;
            SelectionBox.IsVisible = false;
            EndCanvasPan();
        }

        private void BeginCanvasPan(Point viewPoint)
        {
            _isCanvasPanning = true;
            _canvasPanStart = viewPoint;
            _canvasScrollStart = new Point(CanvasScrollView.ScrollX, CanvasScrollView.ScrollY);
        }

        private void UpdateCanvasPan(Point currentPoint)
        {
            if (!_isCanvasPanning)
            {
                return;
            }

            var delta = new Point(currentPoint.X - _canvasPanStart.X, currentPoint.Y - _canvasPanStart.Y);
            var targetX = Math.Max(0, _canvasScrollStart.X - delta.X);
            var targetY = Math.Max(0, _canvasScrollStart.Y - delta.Y);

            MainThread.BeginInvokeOnMainThread(async () => await CanvasScrollView.ScrollToAsync(targetX, targetY, false));
        }

        private void EndCanvasPan()
        {
            _isCanvasPanning = false;
        }

        /// <summary>
        /// Returns the top-most block whose bounds contain the given point, or null.
        /// </summary>
        private ReconstructionConfigurationBlock? FindBlockAtPoint(Point point)
        {
            return _viewModel.Blocks.FirstOrDefault(block =>
                point.X >= block.X && point.X <= block.X + block.Width &&
                point.Y >= block.Y && point.Y <= block.Y + block.Height);
        }

        /// <summary>
        /// Euclidean distance between two MAUI Points.
        /// </summary>
        private static double Distance(Point a, Point b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private double GetPortCenterY(ReconstructionConfigurationBlock block)
        {
            var desiredCenter = PortMarginTop + (PortSize * 0.5);
            return Math.Min(block.Height - PortSize * 0.5, desiredCenter);
        }

        /// <summary>
        /// Left-side input port center in view coordinates.
        /// </summary>
        private Point GetInputPortAnchor(ReconstructionConfigurationBlock block)
            => new(block.X, block.Y + GetPortCenterY(block));

        /// <summary>
        /// Right-side output port center in view coordinates.
        /// </summary>
        private Point GetOutputPortAnchor(ReconstructionConfigurationBlock block)
            => new(block.X + block.Width, block.Y + GetPortCenterY(block));

        /// <summary>
        /// Attempts to select a connection by clicking near the geometric midpoint of its curve.
        /// The closest midpoint under a threshold is chosen.
        /// </summary>
        private bool TrySelectConnection(SKPoint clickPoint)
        {
            ReconstructionConnection bestMatch = null;
            double minDistance = 20.0;

            foreach (var conn in _viewModel.Connections)
            {
                if (conn.Source == null || conn.Target == null) continue;

                var sourceAnchor = ScalePoint(GetOutputPortAnchor(conn.Source));
                var targetAnchor = ScalePoint(GetInputPortAnchor(conn.Target));

                float x1 = (float)sourceAnchor.X;
                float y1 = (float)sourceAnchor.Y;
                float x2 = (float)targetAnchor.X;
                float y2 = (float)targetAnchor.Y;

                // Use straight midpoint for hit-testing simplicity (not actual Bezier mid)
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

        /// <summary>
        /// Draws a subtle dotted grid as the background of the canvas (purely cosmetic).
        /// </summary>
        private void OnGridPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear();

            var spacing = (float)Math.Max(4, _viewModel.GridSpacing * _canvasScale);
            using var dotPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = SKColor.Parse("#2F3640"),
                IsAntialias = true
            };

            for (float x = spacing / 2f; x < e.Info.Width; x += spacing)
            {
                for (float y = spacing / 2f; y < e.Info.Height; y += spacing)
                {
                    canvas.DrawCircle(x, y, 1.5f, dotPaint);
                }
            }
        }

        /// <summary>
        /// Paints all committed connections and any temporary connection being dragged.
        /// Adds style variations for selection and weight requirements.
        /// </summary>
        private void OnCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear();

            // Base paint for normal connections
            using var paintNormal = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColor.Parse("#F72585"),
                StrokeWidth = 3,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };

            // Highlight paint for selected connections
            using var paintSelected = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Orange,
                StrokeWidth = 5,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };

            // Paint for connections that require a weight label
            using var weightPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Yellow,
                StrokeWidth = 4,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };

            // Draw each connection as a cubic Bezier between port anchors
            foreach (var conn in _viewModel.Connections)
            {
                if (conn.Source == null || conn.Target == null) continue;

                var sourceAnchor = ScalePoint(GetOutputPortAnchor(conn.Source));
                var targetAnchor = ScalePoint(GetInputPortAnchor(conn.Target));

                float x1 = (float)sourceAnchor.X;
                float y1 = (float)sourceAnchor.Y;
                float x2 = (float)targetAnchor.X;
                float y2 = (float)targetAnchor.Y;

                // Select style based on selection and weight requirement
                var paintToUse = conn.IsSelected
                    ? paintSelected
                    : conn.RequiresWeight
                        ? weightPaint
                        : paintNormal;

                // Clone so we can attach a dashed path effect without mutating shared instances
                using var styledPaint = paintToUse.Clone();
                // Example of special styling: Optimizer -> Model is dashed
                styledPaint.PathEffect = (conn.Source.Type == BlockType.Optimizer && conn.Target.Type == BlockType.Model)
                                       ? SKPathEffect.CreateDash(new float[] { 6, 6 }, 0)
                                       : paintToUse.PathEffect;

                DrawConnectionCurve(canvas, styledPaint, x1, y1, x2, y2);

                // Optional weight label centered (approx) on the curve
                if (conn.RequiresWeight)
                {
                    var label = $"{conn.Weight:0.##}";
                    using var textPaint = new SKPaint
                    {
                        Color = SKColors.White,
                        IsAntialias = true,
                        TextSize = 16,
                        Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
                    };

                    float labelX = (x1 + x2) / 2f;
                    float labelY = (y1 + y2) / 2f - 6;
                    var textBounds = new SKRect();
                    textPaint.MeasureText(label, ref textBounds);
                    var padding = 6f;

                    using var bgPaint = new SKPaint
                    {
                        Color = SKColor.Parse("#66000000"),
                        IsAntialias = true
                    };
                    var rect = SKRect.Create(labelX - textBounds.MidX - padding, labelY + textBounds.Top - padding,
                        textBounds.Width + 2 * padding, textBounds.Height + 2 * padding);
                    canvas.DrawRoundRect(rect, 6, 6, bgPaint);
                    canvas.DrawText(label, labelX - textBounds.MidX, labelY, textPaint);
                }
            }

            // Draw the temporary connection being dragged (dashed white)
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

                var start = ScalePoint(_tempConnectionStart.Value);
                var end = ScalePoint(_tempConnectionEnd.Value);

                float x1 = (float)start.X;
                float y1 = (float)start.Y;
                float x2 = (float)end.X;
                float y2 = (float)end.Y;

                DrawConnectionCurve(canvas, tempPaint, x1, y1, x2, y2);
            }
        }

        /// <summary>
        /// Converts a SkiaSharp point (in canvas pixels) to the corresponding MAUI view coordinate,
        /// accounting for canvas size vs. the view's layout size.
        /// </summary>
        private Point ToViewPoint(SKPoint skPoint)
        {
            var canvasSize = ConnectionsCanvas.CanvasSize;
            var viewWidth = ConnectionsCanvas.Width;
            var viewHeight = ConnectionsCanvas.Height;

            if (canvasSize.Width <= 0 || canvasSize.Height <= 0 || viewWidth <= 0 || viewHeight <= 0)
            {
                // Fallback: best effort mapping
                return new Point(skPoint.X, skPoint.Y);
            }

            double x = skPoint.X * viewWidth / canvasSize.Width;
            double y = skPoint.Y * viewHeight / canvasSize.Height;
            return new Point(x, y);
        }

        private Point ToLogicalPoint(SKPoint skPoint)
        {
            var viewPoint = ToViewPoint(skPoint);
            return new Point(viewPoint.X / _canvasScale, viewPoint.Y / _canvasScale);
        }

        /// <summary>
        /// Draws a cubic Bezier from source (x1,y1) to target (x2,y2) with horizontal control points.
        /// This creates a smooth "S"-shaped curve between ports.
        /// </summary>
        private void DrawConnectionCurve(SKCanvas canvas, SKPaint paint, float x1, float y1, float x2, float y2)
        {
            using var path = new SKPath();
            path.MoveTo(x1, y1);
            float cp1X = x1 + 60 * (float)_canvasScale;  // Pull right from source
            float cp2X = x2 - 60 * (float)_canvasScale;  // Pull left toward target
            path.CubicTo(cp1X, y1, cp2X, y2, x2, y2);
            canvas.DrawPath(path, paint);
        }

        /// <summary>
        /// Mesh preview canvas paint. Detects discretization type and dispatches to LBM/FEM renderers.
        /// </summary>
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

        /// <summary>
        /// Simple LBM view: draw each cell with fill color by type and a light grid stroke.
        /// </summary>
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

        /// <summary>
        /// Maps element conductivity to a red/blue diverging color scale centered at the mid.
        /// </summary>
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

        /// <summary>
        /// FEM preview: draw filled triangles colored by conductivity, outline, and electrode overlays.
        /// </summary>
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

            // Local function mapping FEM vertex to canvas coordinates (Y-up to Y-down conversion applied)
            SKPoint ToCanvas(Utility.Classes.Discretizer.FiniteElementMesh.FEMVertex v)
                => new((float)(v.X - minX) * scale + marginX,
                        info.Height - ((float)(v.Y - minY) * scale + marginY));

            var elements = mesh.ElementsTyped;
            double min = elements.Min(el => el.Conductivity);
            double max = elements.Max(el => el.Conductivity);

            using var path = new SKPath();
            foreach (var el in elements)
            {
                // Fill each triangle by conductivity color, then stroke outline
                var p1 = ToCanvas(el.Vertices[0]);
                var p2 = ToCanvas(el.Vertices[1]);
                var p3 = ToCanvas(el.Vertices[2]);
                path.Reset();
                path.MoveTo(p1); path.LineTo(p2); path.LineTo(p3); path.Close();
                _femFill.Color = ColorForValue(el.Conductivity, min, max);
                canvas.DrawPath(path, _femFill);
                canvas.DrawPath(path, _femStroke);
            }

            // Stroke electrode line segments along the boundary
            foreach (var segment in mesh.GetElectrodeSegments())
            {
                var start = ToCanvas(segment.Start);
                var end = ToCanvas(segment.End);
                canvas.DrawLine(start, end, _electrodeSegmentStroke);
            }

            // Draw point electrodes as small yellow circles
            foreach (var v in mesh.Vertices.Where(v => v.IsElectrode))
                canvas.DrawCircle(ToCanvas(v), 3f, _electrodeFill);
        }

        /// <summary>
        /// Toolbar/command handler to add a new block at a default position.
        /// </summary>
        private void OnAddBlockClicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is BlockType type)
            {
                _viewModel.AddBlock(type, 100, 100);
            }
        }

        private void OnZoomInClicked(object sender, EventArgs e) => AdjustZoom(0.1);

        private void OnZoomOutClicked(object sender, EventArgs e) => AdjustZoom(-0.1);

        private void AdjustZoom(double delta)
        {
            var newScale = Math.Clamp(_canvasScale + delta, MinCanvasScale, MaxCanvasScale);
            if (Math.Abs(newScale - _canvasScale) < 0.001)
            {
                return;
            }

            _canvasScale = newScale;
            ApplyCanvasScale();
        }
    }
}