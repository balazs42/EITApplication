using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using System.Linq;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace ElectricalImpedanceTomography.Views
{
    public partial class PostProcessingPage : ContentPage
    {
        private readonly PostProcessingPageViewModel _viewModel;

        // Visual state
        private float _scale = 1.0f;

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
            _viewModel.MeshUpdated += OnMeshUpdated;
        }

        private void OnMeshUpdated(object sender, EventArgs e)
        {
            _scale = 1.0f;
            MainThread.BeginInvokeOnMainThread(RequestRedraw);
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (!_viewModel.HasMesh)
                _viewModel.LoadLatestWorkspaceResult();
        }

        private void RequestRedraw()
        {
            DiscretizationCanvas.InvalidateSurface();
            HistogramCanvas.InvalidateSurface();
        }

        // --- Interaction Handlers ---

        private void OnZoomIn(object sender, EventArgs e) { _scale *= 1.1f; DiscretizationCanvas.InvalidateSurface(); }
        private void OnZoomOut(object sender, EventArgs e) { _scale *= 0.9f; DiscretizationCanvas.InvalidateSurface(); }

        private void OnFitView(object sender, EventArgs e)
        {
            _scale = 1.0f;
            DiscretizationCanvas.InvalidateSurface();
        }

        private void OnCanvasTouch(object sender, SKTouchEventArgs e)
        {
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

        // --- Rendering: Mesh / Grid ---

        private void OnDiscretizationPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var info = e.Info;

            var femMesh = _viewModel.FemMesh;
            var lbmGrid = _viewModel.LbmGrid;

            if (!_viewModel.HasMesh || (femMesh == null && lbmGrid == null))
            {
                canvas.Clear(SKColors.Transparent);
                var message = string.IsNullOrWhiteSpace(_viewModel.CanvasMessage)
                    ? "No reconstruction loaded."
                    : _viewModel.CanvasMessage;
                var center = new SKPoint(info.Width / 2f, info.Height / 2f);
                canvas.DrawText(message, center.X, center.Y, _messagePaint);
                return;
            }

            if (femMesh != null)
            {
                DrawFemMesh(canvas, info, femMesh);
            }
            else if (lbmGrid != null)
            {
                DrawLbmGrid(canvas, info, lbmGrid);
            }
        }

        private void DrawFemMesh(SKCanvas canvas, SKImageInfo info, FEMMesh mesh)
        {
            double minX = mesh.Vertices.Min(v => v.X);
            double maxX = mesh.Vertices.Max(v => v.X);
            double minY = mesh.Vertices.Min(v => v.Y);
            double maxY = mesh.Vertices.Max(v => v.Y);

            float extent = (float)Math.Max(maxX - minX, maxY - minY);
            if (extent < 1e-6f)
                extent = 1f;

            canvas.Translate(info.Width / 2f, info.Height / 2f);
            float baseScale = 0.85f * Math.Min(info.Width, info.Height) / extent;
            canvas.Scale(_scale * baseScale);
            canvas.Translate(-(float)((minX + maxX) / 2.0), -(float)((minY + maxY) / 2.0));

            foreach (var element in mesh.ElementsTyped)
            {
                var vertices = element.Vertices;
                double value = _viewModel.ProcessValue(_viewModel.GetConductivityValue(element.Id, element.Conductivity));
                _fillPaint.Color = GetHeatColor(value, _viewModel.SelectedColormap);

                using var path = new SKPath();
                path.MoveTo((float)vertices[0].X, (float)vertices[0].Y);
                path.LineTo((float)vertices[1].X, (float)vertices[1].Y);
                path.LineTo((float)vertices[2].X, (float)vertices[2].Y);
                path.Close();

                canvas.DrawPath(path, _fillPaint);
                if (_viewModel.ShowWireframe)
                    canvas.DrawPath(path, _wireframePaint);
            }

            if (_viewModel.ShowNodes)
            {
                float radius = 3f / (_scale * baseScale);
                foreach (var vertex in mesh.Vertices)
                {
                    canvas.DrawCircle((float)vertex.X, (float)vertex.Y, radius, _nodePaint);
                }
            }
        }

        private void DrawLbmGrid(SKCanvas canvas, SKImageInfo info, LBMGrid grid)
        {
            float width = grid.Nx;
            float height = grid.Ny;

            canvas.Translate(info.Width / 2f, info.Height / 2f);
            float baseScale = 0.85f * Math.Min(info.Width, info.Height) / Math.Max(width, height);
            canvas.Scale(_scale * baseScale);
            canvas.Translate(-width / 2f, -height / 2f);

            foreach (var element in grid.GetElements().Cast<LBMElement>())
            {
                if (element.IsWall)
                    continue;

                var (x, y) = grid.ToLattice(element.Id);
                double value = _viewModel.ProcessValue(_viewModel.GetConductivityValue(element.Id, element.Conductivity));
                _fillPaint.Color = GetHeatColor(value, _viewModel.SelectedColormap);

                var rect = SKRect.Create((float)x, (float)y, 1f, 1f);
                canvas.DrawRect(rect, _fillPaint);
                if (_viewModel.ShowWireframe)
                    canvas.DrawRect(rect, _wireframePaint);

                if (_viewModel.ShowNodes)
                {
                    float radius = 0.12f;
                    canvas.DrawCircle((float)(x + 0.5f), (float)(y + 0.5f), radius, _nodePaint);
                }
            }
        }

        // --- Rendering: Histogram ---

        private void OnHistogramPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var values = _viewModel.ElementConductivities().ToList();
            if (values.Count == 0) return;

            // Calc Bins
            int bins = 24;
            int[] counts = new int[bins];
            foreach (var val in values)
            {
                double v = _viewModel.ProcessValue(val); // 0-1 normalized
                int bin = (int)(v * bins);
                if (bin >= bins) bin = bins - 1;
                if (bin < 0) bin = 0;
                counts[bin]++;
            }

            int maxCount = counts.Max();
            if (maxCount == 0)
                return;
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