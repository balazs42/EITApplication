using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using System.Linq;

namespace ElectricalImpedanceTomography.Views
{
    public partial class PostProcessingPage : ContentPage
    {
        private readonly PostProcessingPageViewModel _viewModel;
        private readonly SKPaint _femStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(80), StrokeWidth = 0.75f, IsAntialias = true };
        private readonly SKPaint _femFill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
        private readonly SKPaint _lbmStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Gray.WithAlpha(120), StrokeWidth = 1f, IsAntialias = true };
        private readonly SKPaint _legendStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(80), StrokeWidth = 1f, IsAntialias = true };
        private readonly SKPaint _legendText = new() { Color = SKColors.LightGray, TextSize = 12, IsAntialias = true };
        private readonly SKPaint _placeholderText = new()
        {
            Color = SKColors.LightGray,
            TextSize = 28,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        private float _scale, _marginX, _marginY, _minX, _minY, _meshWidth, _meshHeight;

        public PostProcessingPage()
        {
            InitializeComponent();
            _viewModel = new PostProcessingPageViewModel();
            BindingContext = _viewModel;

            _viewModel.MeshUpdated += (_, _) => PostProcessingCanvas?.InvalidateSurface();
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PostProcessingPageViewModel.MinCutoff)
                    || args.PropertyName == nameof(PostProcessingPageViewModel.MaxCutoff)
                    || args.PropertyName == nameof(PostProcessingPageViewModel.IsLogScale)
                    || args.PropertyName == nameof(PostProcessingPageViewModel.HasMesh))
                {
                    ColorBarCanvas?.InvalidateSurface();
                }
            };
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (!_viewModel.HasMesh)
            {
                _viewModel.LoadLatestWorkspaceResult();
            }

            PostProcessingCanvas?.InvalidateSurface();
            ColorBarCanvas?.InvalidateSurface();
        }

        private void OnCanvasViewPaintSurface(object? sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Transparent);

            if (!_viewModel.HasMesh)
            {
                DrawPlaceholder(canvas, info, _viewModel.CanvasMessage);
                return;
            }

            var conductivities = _viewModel.ElementConductivities().ToList();
            if (conductivities.Count == 0)
            {
                DrawPlaceholder(canvas, info, "No conductivity data available.");
                return;
            }

            if (_viewModel.LbmGrid is LBMGrid lbm)
            {
                DrawLbmGrid(canvas, info, lbm);
                ColorBarCanvas?.InvalidateSurface();
                return;
            }

            if (_viewModel.FemMesh is FEMMesh fem)
            {
                DrawFemMesh(canvas, info, fem);
                ColorBarCanvas?.InvalidateSurface();
                return;
            }

            DrawPlaceholder(canvas, info, "Mesh could not be rendered.");
        }

        private void DrawLbmGrid(SKCanvas canvas, SKImageInfo info, LBMGrid grid)
        {
            float cellW = (float)info.Width / grid.Nx;
            float cellH = (float)info.Height / grid.Ny;

            foreach (var el in grid.GetElements().Cast<LBMElement>())
            {
                var (x, y) = grid.ToLattice(el.Id);
                var rect = SKRect.Create(x * cellW, y * cellH, cellW, cellH);
                double sigma = _viewModel.GetConductivityValue(el.Id, el.Conductivity);
                var color = ColorForValue(_viewModel.ProcessValue(sigma));

                using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = color, IsAntialias = true };
                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, _lbmStroke);
            }
        }

        private void DrawFemMesh(SKCanvas canvas, SKImageInfo info, FEMMesh mesh)
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

            using var path = new SKPath();

            foreach (var el in mesh.ElementsTyped)
            {
                var p1 = ToCanvas(el.Vertices[0]);
                var p2 = ToCanvas(el.Vertices[1]);
                var p3 = ToCanvas(el.Vertices[2]);

                path.Reset();
                path.MoveTo(p1);
                path.LineTo(p2);
                path.LineTo(p3);
                path.Close();

                double sigma = _viewModel.GetConductivityValue(el.Id, el.Conductivity);
                _femFill.Color = ColorForValue(_viewModel.ProcessValue(sigma));
                canvas.DrawPath(path, _femFill);
                canvas.DrawPath(path, _femStroke);
            }
        }

        private SKPoint ToCanvas(FEMVertex v) => new((float)(v.X - _minX) * _scale + _marginX, PostProcessingCanvas.CanvasSize.Height - ((float)(v.Y - _minY) * _scale + _marginY));

        private SKColor ColorForValue(double normalized)
        {
            float t = (float)Math.Clamp(normalized, 0, 1);
            byte r = (byte)(255 * t);
            byte b = (byte)(255 * (1 - t));
            return new SKColor(r, 20, b, 255);
        }

        private void DrawPlaceholder(SKCanvas canvas, SKImageInfo info, string message)
        {
            canvas.DrawText(string.IsNullOrWhiteSpace(message) ? "No reconstruction available." : message,
                            info.Width / 2f,
                            info.Height / 2f,
                            _placeholderText);
        }

        private void OnColorBarPaintSurface(object? sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Transparent);

            if (!_viewModel.HasMesh)
                return;

            var barRect = SKRect.Create(info.Width * 0.25f, 10, info.Width * 0.35f, info.Height - 30);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(barRect.Left, barRect.Bottom),
                new SKPoint(barRect.Left, barRect.Top),
                new[] { ColorForValue(0), ColorForValue(1) },
                null,
                SKShaderTileMode.Clamp);

            using var fill = new SKPaint { Shader = shader, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRect(barRect, fill);
            canvas.DrawRect(barRect, _legendStroke);

            float labelX = barRect.Right + 6f;
            string minText = _viewModel.MinCutoff.ToString("0.###");
            string maxText = _viewModel.MaxCutoff.ToString("0.###");
            string midText = ((_viewModel.MinCutoff + _viewModel.MaxCutoff) / 2.0).ToString("0.###");

            canvas.DrawText(maxText, labelX, barRect.Top + _legendText.TextSize, _legendText);
            canvas.DrawText(midText, labelX, barRect.MidY + _legendText.TextSize * 0.35f, _legendText);
            canvas.DrawText(minText, labelX, barRect.Bottom, _legendText);
        }
    }
}
