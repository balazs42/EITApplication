using ElectricalImpedanceTomography.ViewModels;
using ElectricalImpedanceTomography.Extensions;
using SkiaSharp;
using Microsoft.Maui.Graphics;
using Utility.Classes.Application;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using System.Collections.Specialized;

namespace ElectricalImpedanceTomography.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainPageViewModel _viewModel;

        // Paints for Mesh (Stylized)
        private readonly SKPaint _lbmFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black, IsAntialias = true };
        private readonly SKPaint _lbmWall = new() { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
        private readonly SKPaint _lbmElectrode = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange, IsAntialias = true };
        private readonly SKPaint _lbmStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1, IsAntialias = true };

        // FEM: Thinner strokes, Anti-aliased, Vibrant fill
        private readonly SKPaint _femStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 0.5f, IsAntialias = true };
        private readonly SKPaint _femFill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
        private readonly SKPaint _electrodeFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Yellow, IsAntialias = true };
        private readonly SKPaint _electrodeSegmentStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Gold, StrokeWidth = 3, IsAntialias = true };

        // Paints for HEADER Text
        private readonly SKPaint _textPaint = new()
        {
            TextSize = 80,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
            TextAlign = SKTextAlign.Center
        };

        private float _scale, _marginX, _marginY, _minX, _minY, _meshWidth, _meshHeight;

        public MainPage()
        {
            InitializeComponent();

            // Set the background drawable for dots
            BackgroundGraphics.Drawable = new DotPatternDrawable();

            _viewModel = Utility.Composition.Container.ResolveObject<MainPageViewModel>();
            BindingContext = _viewModel;
            _viewModel.DebugLog.CollectionChanged += OnDebugLogChanged;
            _viewModel.MeshUpdated += () => MeshCanvasView?.InvalidateSurface();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            MeshCanvasView?.InvalidateSurface();
            HeaderCanvasView?.InvalidateSurface();
            if (ConsoleScroll != null && ConsoleStack != null)
                MainThread.BeginInvokeOnMainThread(async () => await ConsoleScroll.ScrollToAsync(0, ConsoleStack.Height, false));
        }

        // --- DRAWING THE DOT PATTERN ---
        private class DotPatternDrawable : IDrawable
        {
            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                canvas.SaveState();
                canvas.FillColor = Color.FromHex("#334155"); // Slate-700
                float spacing = 40;
                float radius = 1;

                for (float x = 0; x < dirtyRect.Width; x += spacing)
                {
                    for (float y = 0; y < dirtyRect.Height; y += spacing)
                    {
                        canvas.FillCircle(x, y, radius);
                    }
                }
                canvas.RestoreState();
            }
        }

        // --- DRAWING THE GRADIENT TEXT ---
        private void OnHeaderPaintSurface(object sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Transparent);

            string text = "ITERATE";
            float x = info.Width / 2;
            float y = (info.Height + _textPaint.TextSize) / 2 - 10; // Center vertically roughly

            // Create Gradient Shader
            var colors = new SKColor[] { SKColor.Parse("#e2e8f0"), SKColor.Parse("#64748b") }; // Slate-200 to Slate-500
            var points = new SKPoint[] { new SKPoint(x - 200, y), new SKPoint(x + 200, y) };

            using (var shader = SKShader.CreateLinearGradient(points[0], points[1], colors, null, SKShaderTileMode.Clamp))
            {
                _textPaint.Shader = shader;
                canvas.DrawText(text, x, y, _textPaint);
            }
        }

        // --- STANDARD EVENTS & MESH DRAWING (Updated for style) ---
        private void OnLoadMeasurementClicked(object sender, EventArgs e) => _viewModel.OnLoadMeasurementClicked(sender, e);

        private void OnLoadMeshClicked(object sender, EventArgs e)
        {
            _viewModel.OnLoadMeshClicked(sender, e);
            MeshCanvasView?.InvalidateSurface();
        }

        private void OnConnectButtonClicked(object sender, EventArgs e) => _viewModel.OnConnectButtonClicked(sender, e);

        private void OnConsoleEntryCompleted(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_viewModel.ConsoleInput))
                _viewModel.SendConsoleMessageCommand.Execute(null);
        }

        private void OnDebugLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (ConsoleScroll == null || ConsoleStack == null) return;
            MainThread.BeginInvokeOnMainThread(async () => await ConsoleScroll.ScrollToAsync(0, ConsoleStack.Height, false));
        }

        private async void OnNavigationMenuTapped(object sender, TappedEventArgs e)
        {
            if (sender is VisualElement v)
            {
                await v.ScaleTo(0.95, 50);
                await v.ScaleTo(1.0, 50);
            }
            if (e.Parameter is string page) _viewModel.NavigateCommand.Execute(page);
        }

        private void OnCanvasViewPaintSurface(object? sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Transparent);

            var discretization = Workspace.GetDiscretization();
            if (discretization is LBMGrid lbm) DrawLBMGrid(canvas, info, lbm);
            else if (discretization is FEMMesh fem) DrawFEMMesh(canvas, info, fem);
        }

        // NEW: Vibrant Red-Blue Color Mapping for Style
        private static SKColor ColorForValue(double val, double min, double max)
        {
            if (max == min) return SKColors.Red; // Avoid division by zero

            // Normalize to 0..1
            float t = (float)((val - min) / (max - min));
            t = Math.Clamp(t, 0f, 1f);

            // Simple "Heatmap" style: Blue (low) -> Red (high)
            // Interpolate Red and Blue components
            byte r = (byte)(255 * t);
            byte b = (byte)(255 * (1 - t));

            // Ensure colors are vibrant (0 Green)
            return new SKColor(r, 0, b, 255);
        }

        private void DrawLBMGrid(SKCanvas canvas, SKImageInfo info, LBMGrid grid)
        {
            float cellW = (float)info.Width / grid.Nx;
            float cellH = (float)info.Height / grid.Ny;

            for (int y = 0; y < grid.Ny; y++)
            {
                for (int x = 0; x < grid.Nx; x++)
                {
                    var el = grid.GetElementAt(x, y);
                    SKPaint fill = el.IsElectrode ? _lbmElectrode : el.IsWall ? _lbmWall : _lbmFill;
                    var r = SKRect.Create(x * cellW, y * cellH, cellW, cellH);
                    canvas.DrawRect(r, fill);
                    canvas.DrawRect(r, _lbmStroke);
                }
            }
        }

        private SKPoint ToCanvas(FEMVertex v) => new((float)(v.X - _minX) * _scale + _marginX, MeshCanvasView.CanvasSize.Height - ((float)(v.Y - _minY) * _scale + _marginY));

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

            using var path = new SKPath();

            // Draw Elements (Fill + Thin Stroke)
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

            // Draw Electrode Segments
            foreach (var segment in mesh.GetElectrodeSegments())
            {
                var start = ToCanvas(segment.Start);
                var end = ToCanvas(segment.End);
                canvas.DrawLine(start, end, _electrodeSegmentStroke);
            }

            // Draw Electrode Points
            foreach (var v in mesh.Vertices.Where(v => v.IsElectrode))
                canvas.DrawCircle(ToCanvas(v), 4f, _electrodeFill);
        }
    }
}
