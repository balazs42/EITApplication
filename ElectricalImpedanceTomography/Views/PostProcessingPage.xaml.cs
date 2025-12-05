using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace ElectricalImpedanceTomography.Views
{
    public partial class PostProcessingPage : ContentPage
    {
        private readonly PostProcessingPageViewModel _viewModel;

        // Visual state
        private float _scale = 1.0f;
        private float _translateX = 0f;
        private float _translateY = 0f;
        private SKPoint _lastTouchPoint;

        // Cached Paints
        private readonly SKPaint _gridPaint = new() { Color = SKColor.Parse("#334155"), StrokeWidth = 1, IsAntialias = false };
        private readonly SKPaint _wireframePaint = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Black.WithAlpha(40), StrokeWidth = 0.5f, IsAntialias = true };
        private readonly SKPaint _nodePaint = new() { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
        private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
        private readonly SKPaint _borderPaint = new() { Style = SKPaintStyle.Stroke, Color = SKColor.Parse("#475569"), StrokeWidth = 2, IsAntialias = true };
        private readonly SKPaint _histogramBarPaint = new() { Color = SKColor.Parse("#22d3ee"), Style = SKPaintStyle.Fill, IsAntialias = true };
        private readonly SKPaint _messagePaint = new()
        {
            Color = SKColors.LightGray,
            TextSize = 18,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        public PostProcessingPage()
        {
            InitializeComponent();
            _viewModel = new PostProcessingPageViewModel();
            BindingContext = _viewModel;

            // Redraw triggers
            _viewModel.PropertyChanged += (s, e) => RequestRedraw();
            _viewModel.MeshUpdated += (s, e) => RequestRedraw();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (!_viewModel.HasMesh)
                _viewModel.LoadLatestWorkspaceResult();
        }

        private void RequestRedraw()
        {
            FemCanvas.InvalidateSurface();
            HistogramCanvas.InvalidateSurface();
        }

        // --- Interaction Handlers ---

        private void OnZoomIn(object sender, EventArgs e) { _scale *= 1.1f; FemCanvas.InvalidateSurface(); }
        private void OnZoomOut(object sender, EventArgs e) { _scale *= 0.9f; FemCanvas.InvalidateSurface(); }

        private void OnFitView(object sender, EventArgs e)
        {
            _scale = 1.0f;
            _translateX = 0;
            _translateY = 0;
            FemCanvas.InvalidateSurface();
        }

        private void OnCanvasTouch(object sender, SKTouchEventArgs e)
        {
            switch (e.ActionType)
            {
                case SKTouchAction.Pressed:
                    _lastTouchPoint = e.Location;
                    break;
                case SKTouchAction.Moved:
                    _translateX += e.Location.X - _lastTouchPoint.X;
                    _translateY += e.Location.Y - _lastTouchPoint.Y;
                    _lastTouchPoint = e.Location;
                    FemCanvas.InvalidateSurface();
                    break;
            }
            e.Handled = true;
        }

        // --- Rendering: Background Grid ---

        private void OnBackgroundPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColor.Parse("#0b1120")); // Matches BgMain

            // Draw dotted grid
            int spacing = 24;
            for (int x = 0; x < e.Info.Width; x += spacing)
            {
                for (int y = 0; y < e.Info.Height; y += spacing)
                {
                    canvas.DrawPoint(x, y, _gridPaint);
                }
            }
        }

        // --- Rendering: FEM Mesh ---

        private void OnFemPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var info = e.Info;

            if (!_viewModel.HasMesh || _viewModel.Elements.Count == 0)
            {
                canvas.Clear(SKColor.Parse("#0b1120"));
                var message = string.IsNullOrWhiteSpace(_viewModel.CanvasMessage)
                    ? "No reconstruction loaded."
                    : _viewModel.CanvasMessage;
                var center = new SKPoint(info.Width / 2f, info.Height / 2f);
                canvas.DrawText(message, center.X, center.Y, _messagePaint);
                return;
            }

            // Transform View
            canvas.Translate(info.Width / 2 + _translateX, info.Height / 2 + _translateY);
            float baseScale = Math.Min(info.Width, info.Height) / 2.5f;
            canvas.Scale(_scale * baseScale);

            // Draw Elements
            foreach (var el in _viewModel.Elements)
            {
                if (el.NodeIndices.Length < 3)
                    continue;

                var n1 = _viewModel.Nodes[el.NodeIndices[0]];
                var n2 = _viewModel.Nodes[el.NodeIndices[1]];
                var n3 = _viewModel.Nodes[el.NodeIndices[2]];

                // Calculate Color based on element value
                double val = _viewModel.ProcessValue(el.Value);
                _fillPaint.Color = GetHeatColor(val, _viewModel.SelectedColormap);

                using var path = new SKPath();
                path.MoveTo((float)n1.X, (float)n1.Y);
                path.LineTo((float)n2.X, (float)n2.Y);
                path.LineTo((float)n3.X, (float)n3.Y);
                path.Close();

                canvas.DrawPath(path, _fillPaint);
                if (_viewModel.ShowWireframe) canvas.DrawPath(path, _wireframePaint);
            }

            // Draw Boundary
            canvas.DrawCircle(0, 0, 1.0f, _borderPaint);

            // Draw Nodes
            if (_viewModel.ShowNodes)
            {
                float radius = 3f / (_scale * baseScale);
                foreach (var n in _viewModel.Nodes)
                {
                    canvas.DrawCircle((float)n.X, (float)n.Y, radius, _nodePaint);
                }
            }
        }

        // --- Rendering: Histogram ---

        private void OnHistogramPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            if (_viewModel.Elements.Count == 0) return;

            // Calc Bins
            int bins = 24;
            int[] counts = new int[bins];
            foreach (var el in _viewModel.Elements)
            {
                double v = _viewModel.ProcessValue(el.Value); // 0-1 normalized
                int bin = (int)(v * bins);
                if (bin >= bins) bin = bins - 1;
                if (bin < 0) bin = 0;
                counts[bin]++;
            }

            int maxCount = counts.Max();
            float barW = e.Info.Width / (float)bins;

            for (int i = 0; i < bins; i++)
            {
                float h = (counts[i] / (float)maxCount) * e.Info.Height;
                // Fade color slightly based on height for effect
                _histogramBarPaint.Color = SKColor.Parse("#22d3ee").WithAlpha((byte)(100 + (h / e.Info.Height) * 155));

                var rect = SKRect.Create(i * barW, e.Info.Height - h, barW - 2, h);
                canvas.DrawRect(rect, _histogramBarPaint);
            }
        }

        // --- Colormap Helper ---
        private SKColor GetHeatColor(double norm, string colormap)
        {
            norm = Math.Clamp(norm, 0, 1);
            byte r = 0, g = 0, b = 0;

            if (colormap == "Gray")
            {
                byte c = (byte)(norm * 255);
                return new SKColor(c, c, c);
            }
            else if (colormap == "Hot")
            {
                r = (byte)(norm < 0.33 ? norm * 3 * 255 : 255);
                g = (byte)(norm < 0.33 ? 0 : (norm < 0.66 ? (norm - 0.33) * 3 * 255 : 255));
                b = (byte)(norm < 0.66 ? 0 : (norm - 0.66) * 3 * 255);
            }
            else // Jet
            {
                if (norm < 0.25) { r = 0; g = (byte)(4 * norm * 255); b = 255; }
                else if (norm < 0.5) { r = 0; g = 255; b = (byte)(255 - 4 * (norm - 0.25) * 255); }
                else if (norm < 0.75) { r = (byte)(4 * (norm - 0.5) * 255); g = 255; b = 0; }
                else { r = 255; g = (byte)(255 - 4 * (norm - 0.75) * 255); b = 0; }
            }
            return new SKColor(r, g, b);
        }
    }
}