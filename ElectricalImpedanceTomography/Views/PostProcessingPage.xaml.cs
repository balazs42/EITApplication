using ElectricalImpedanceTomography.ViewModels;
using ElectricalImpedanceTomography.Controls;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using System.Linq;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Rendering;

namespace ElectricalImpedanceTomography.Views
{
    public partial class PostProcessingPage : ContentPage
    {
        private readonly PostProcessingPageViewModel _viewModel;
        private readonly DiscretizationCanvasRenderer _renderer = new();
        private readonly SKPaint _legendStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(80), StrokeWidth = 1f, IsAntialias = true };
        private readonly SKPaint _legendText = new() { Color = SKColors.LightGray, TextSize = 12, IsAntialias = true };
        private readonly SKPaint _placeholderText = new()
        {
            Color = SKColors.LightGray,
            TextSize = 28,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        private float _scale, _marginX, _marginY, _minX, _minY, _meshWidth, _meshHeight, _canvasHeight;
        private string[]? _hoverLines;
        private SKPoint? _hoverPoint;

        public PostProcessingPage()
        {
            InitializeComponent();
            _viewModel = new PostProcessingPageViewModel();
            BindingContext = _viewModel;

            _viewModel.MeshUpdated += (_, _) => PostProcessingCanvas?.InvalidateSurface();
            _viewModel.ImageSaveRequested += (_, args) => SavePostProcessingImage(args);
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PostProcessingPageViewModel.MinCutoff)
                    || args.PropertyName == nameof(PostProcessingPageViewModel.MaxCutoff)
                    || args.PropertyName == nameof(PostProcessingPageViewModel.IsLogScale)
                    || args.PropertyName == nameof(PostProcessingPageViewModel.HasMesh)
                    || args.PropertyName == nameof(PostProcessingPageViewModel.SelectedConductivityDisplayMode))
                {
                    ColorBarCanvas?.InvalidateSurface();
                    PostProcessingCanvas?.InvalidateSurface();
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

            if (_viewModel.FemMesh is FEMMesh fem)
                ComputeFemTransform(fem, info);

            _renderer.Draw(
                canvas,
                info,
                new DiscretizationRenderRequest(_viewModel.Discretization,
                                                DiscretizationRenderMode.Conductivity,
                                                _viewModel.CurrentDistribution),
                CreateRenderOptions(SKColors.Transparent));

            DrawHoverInfo(canvas, info, _hoverLines, _hoverPoint);
            ColorBarCanvas?.InvalidateSurface();
        }

        private void ComputeFemTransform(FEMMesh mesh, SKImageInfo info)
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
            _canvasHeight = info.Height;
        }

        private SKPoint ToCanvas(FEMVertex v)
            => new((float)(v.X - _minX) * _scale + _marginX,
                   _canvasHeight - ((float)(v.Y - _minY) * _scale + _marginY));

        private float Dot(SKPoint a, SKPoint b) => a.X * b.X + a.Y * b.Y;

        private bool PointInTriangle(SKPoint p, SKPoint a, SKPoint b, SKPoint c,
                                     out float u, out float v, out float w)
        {
            var v0 = b - a;
            var v1 = c - a;
            var v2 = p - a;
            float d00 = Dot(v0, v0);
            float d01 = Dot(v0, v1);
            float d11 = Dot(v1, v1);
            float d20 = Dot(v2, v0);
            float d21 = Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            v = (d11 * d20 - d01 * d21) / denom;
            w = (d00 * d21 - d01 * d20) / denom;
            u = 1 - v - w;
            return (u >= 0) && (v >= 0) && (w >= 0);
        }

        private void DrawPlaceholder(SKCanvas canvas, SKImageInfo info, string message)
        {
            canvas.DrawText(string.IsNullOrWhiteSpace(message) ? "No reconstruction available." : message,
                            info.Width / 2f,
                            info.Height / 2f,
                            _placeholderText);
        }

        private void DrawHoverInfo(SKCanvas canvas, SKImageInfo info, string[]? lines, SKPoint? pt)
        {
            if (lines == null || !pt.HasValue)
                return;

            using var txt = new SKPaint { IsAntialias = true, Color = SKColors.White };
            using var font = new SKFont(SKTypeface.Default, 14);
            float w = lines.Max(l => font.MeasureText(l)) + 8;
            float h = lines.Length * (font.Size + 4) + 4;
            var center = new SKPoint(info.Width / 2f, info.Height / 2f);
            var dir = new SKPoint(center.X - pt.Value.X, center.Y - pt.Value.Y);
            const float off = 8f;
            float x = dir.X > 0 ? pt.Value.X + off : pt.Value.X - off - w;
            float y = dir.Y > 0 ? pt.Value.Y + off : pt.Value.Y - off - h;
            var box = new SKRect(x, y, x + w, y + h);
            using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 200), IsAntialias = true };
            canvas.DrawRoundRect(box, 4, 4, bg);
            float ty = box.Top + font.Size + 2;
            foreach (var line in lines)
            {
                canvas.DrawText(line, box.Left + 4, ty, SKTextAlign.Left, font, txt);
                ty += font.Size + 4;
            }
        }

        private DiscretizationCanvasRenderOptions CreateRenderOptions(SKColor backgroundColor)
            => new()
            {
                BackgroundColor = backgroundColor,
                ConductivityDisplayMode = _viewModel.SelectedConductivityDisplayMode,
                UseLogScale = _viewModel.IsLogScale,
                MinimumValueOverride = _viewModel.MinCutoff,
                MaximumValueOverride = _viewModel.MaxCutoff,
                FemStrokeColor = SKColors.White.WithAlpha(80),
                FemStrokeWidth = 0.75f,
                LbmStrokeColor = SKColors.Gray.WithAlpha(120)
            };

        private void OnColorBarPaintSurface(object? sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Transparent);

            if (!_viewModel.HasMesh)
                return;

            _renderer.DrawColorBar(
                canvas,
                info,
                new DiscretizationRenderRequest(_viewModel.Discretization,
                                                DiscretizationRenderMode.Conductivity,
                                                _viewModel.CurrentDistribution),
                CreateRenderOptions(SKColors.Transparent),
                new DiscretizationColorBarOptions
                {
                    BackgroundColor = SKColors.Transparent,
                    BorderColor = _legendStroke.Color,
                    TextColor = _legendText.Color,
                    TextSize = _legendText.TextSize,
                    Orientation = ColorBarOrientation.Vertical,
                    ShowMidpointLabel = true
                });
        }

        private void OnPostProcessingCanvasTouch(object sender, SKTouchEventArgs e)
        {
            if (!_viewModel.HasMesh)
                return;

            if (e.ActionType == SKTouchAction.Released || e.ActionType == SKTouchAction.Cancelled)
            {
                _hoverLines = null;
                _hoverPoint = null;
                ((SKCanvasView)sender).InvalidateSurface();
                e.Handled = true;
                return;
            }

            var view = (SKCanvasView)sender;
            if (_viewModel.FemMesh is FEMMesh fem)
            {
                ComputeFemTransform(fem, new SKImageInfo((int)view.CanvasSize.Width, (int)view.CanvasSize.Height));
                _hoverLines = null;
                _hoverPoint = null;
                foreach (var elem in fem.GetElements().Cast<FEMElement>())
                {
                    var c0 = ToCanvas(elem.Vertices[0]);
                    var c1 = ToCanvas(elem.Vertices[1]);
                    var c2 = ToCanvas(elem.Vertices[2]);
                    if (PointInTriangle(e.Location, c0, c1, c2, out _, out _, out _))
                    {
                        double val = _viewModel.GetConductivityValue(elem.Id, elem.Conductivity);
                        _hoverLines = new[] { $"Elem: {elem.Id}", $"σ: {val:F3}" };
                        _hoverPoint = e.Location;
                        break;
                    }
                }

                view.InvalidateSurface();
                e.Handled = true;
                return;
            }

            if (_viewModel.LbmGrid is LBMGrid lbm)
            {
                float cw = view.CanvasSize.Width / lbm.Nx;
                float ch = view.CanvasSize.Height / lbm.Ny;
                int col = (int)(e.Location.X / cw);
                int row = (int)(e.Location.Y / ch);
                col = Math.Clamp(col, 0, lbm.Nx - 1);
                row = Math.Clamp(row, 0, lbm.Ny - 1);
                var el = lbm.GetElementAt(col, row);
                double val = _viewModel.GetConductivityValue(el.Id, el.Conductivity);
                _hoverLines = new[] { $"ID: {el.Id}", $"σ: {val:F3}" };
                _hoverPoint = e.Location;
                view.InvalidateSurface();
                e.Handled = true;
            }
        }

        private void SavePostProcessingImage(PostProcessingImageSaveRequest request)
        {
            var previousHoverLines = _hoverLines;
            var previousHoverPoint = _hoverPoint;
            _hoverLines = null;
            _hoverPoint = null;

            try
            {
                int width = (int)PostProcessingCanvas.CanvasSize.Width;
                int height = (int)PostProcessingCanvas.CanvasSize.Height;
                if (width <= 0 || height <= 0)
                {
                    width = 900;
                    height = 900;
                }

                using var surface = SKSurface.Create(new SKImageInfo(width, height));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);

                var discretization = _viewModel.Discretization;
                if (discretization == null || _viewModel.CurrentDistribution == null)
                {
                    return;
                }

                _renderer.Draw(
                    canvas,
                    new SKImageInfo(width, height),
                    new DiscretizationRenderRequest(discretization,
                                                    DiscretizationRenderMode.Conductivity,
                                                    _viewModel.CurrentDistribution),
                    CreateRenderOptions(SKColors.Transparent));

                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = System.IO.File.OpenWrite(request.Path);
                data.SaveTo(stream);
                _viewModel.LogExternal($"Saved post-processing image to {request.Path}.", "success");
            }
            catch (Exception ex)
            {
                _viewModel.LogExternal($"Failed to save post-processing image: {ex.Message}", "error");
            }
            finally
            {
                _hoverLines = previousHoverLines;
                _hoverPoint = previousHoverPoint;
            }
        }
    }
}
