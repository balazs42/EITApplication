using ElectricalImpedanceTomography.ViewModels;
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

        private float _scale, _marginX, _marginY, _minX, _minY, _meshWidth, _meshHeight, _canvasHeight;
        private string[]? _hoverLines;
        private SKPoint? _hoverPoint;

        private static readonly (double Position, SKColor Color)[] EnhancedDivergingPalette =
        {
            (0.0, SKColor.Parse("#2B83BA")),
            (0.17, SKColor.Parse("#74ADD1")),
            (0.33, SKColor.Parse("#E0F3F8")),
            (0.5, SKColor.Parse("#FFFFBF")),
            (0.67, SKColor.Parse("#FEE08B")),
            (0.83, SKColor.Parse("#FC8D59")),
            (1.0, SKColor.Parse("#D53E4F"))
        };

        private static readonly (double Position, SKColor Color)[] MatlabJetPalette =
        {
            (0.0, SKColor.Parse("#00007F")),
            (0.125, SKColor.Parse("#0000FF")),
            (0.375, SKColor.Parse("#00FFFF")),
            (0.625, SKColor.Parse("#FFFF00")),
            (0.875, SKColor.Parse("#FF0000")),
            (1.0, SKColor.Parse("#7F0000"))
        };

        private static readonly (double Position, SKColor Color)[] ParulaPalette =
        {
            (0.0, SKColor.Parse("#352A87")),
            (0.16, SKColor.Parse("#2462BE")),
            (0.33, SKColor.Parse("#1F9AD6")),
            (0.5, SKColor.Parse("#3BB6A5")),
            (0.66, SKColor.Parse("#74C476")),
            (0.83, SKColor.Parse("#B6D051")),
            (1.0, SKColor.Parse("#FDE724"))
        };

        private static readonly (double Position, SKColor Color)[] ViridisPalette =
        {
            (0.0, SKColor.Parse("#440154")),
            (0.2, SKColor.Parse("#414487")),
            (0.4, SKColor.Parse("#2A788E")),
            (0.6, SKColor.Parse("#22A884")),
            (0.8, SKColor.Parse("#7AD151")),
            (1.0, SKColor.Parse("#FDE725"))
        };

        private static readonly (double Position, SKColor Color)[] PlasmaPalette =
        {
            (0.0, SKColor.Parse("#0D0887")),
            (0.16, SKColor.Parse("#5B02A3")),
            (0.33, SKColor.Parse("#9A179B")),
            (0.5, SKColor.Parse("#CB4679")),
            (0.66, SKColor.Parse("#ED7953")),
            (0.83, SKColor.Parse("#FDB42F")),
            (1.0, SKColor.Parse("#F0F921"))
        };

        private static readonly (double Position, SKColor Color)[] MagmaPalette =
        {
            (0.0, SKColor.Parse("#000004")),
            (0.16, SKColor.Parse("#1C1044")),
            (0.33, SKColor.Parse("#4F0C6B")),
            (0.5, SKColor.Parse("#822681")),
            (0.66, SKColor.Parse("#B73779")),
            (0.83, SKColor.Parse("#F1605D")),
            (1.0, SKColor.Parse("#FCFDBF"))
        };

        private static readonly (double Position, SKColor Color)[] CividisPalette =
        {
            (0.0, SKColor.Parse("#00204C")),
            (0.2, SKColor.Parse("#00366F")),
            (0.4, SKColor.Parse("#39558C")),
            (0.6, SKColor.Parse("#7B7B78")),
            (0.8, SKColor.Parse("#B8B972")),
            (1.0, SKColor.Parse("#FAF976"))
        };

        private static readonly (double Position, SKColor Color)[] CoolWarmPalette =
        {
            (0.0, SKColor.Parse("#3B4CC0")),
            (0.16, SKColor.Parse("#5C86C5")),
            (0.33, SKColor.Parse("#93B5D7")),
            (0.5, SKColor.Parse("#E6E6E6")),
            (0.66, SKColor.Parse("#E5B08A")),
            (0.83, SKColor.Parse("#D25C4D")),
            (1.0, SKColor.Parse("#8B1A1A"))
        };

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
                var normalized = _viewModel.NormalizeValue(sigma);
                var color = GetConductivityColor(normalized.Working, normalized.Normalized, normalized.Min, normalized.Max);

                using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = color, IsAntialias = true };
                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, _lbmStroke);
            }

            DrawHoverInfo(canvas, info, _hoverLines, _hoverPoint);
        }

        private void DrawFemMesh(SKCanvas canvas, SKImageInfo info, FEMMesh mesh)
        {
            ComputeFemTransform(mesh, info);

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
                var normalized = _viewModel.NormalizeValue(sigma);
                _femFill.Color = GetConductivityColor(normalized.Working, normalized.Normalized, normalized.Min, normalized.Max);
                canvas.DrawPath(path, _femFill);
                canvas.DrawPath(path, _femStroke);
            }

            DrawHoverInfo(canvas, info, _hoverLines, _hoverPoint);
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

        private static SKColor Lerp(SKColor a, SKColor b, double t)
        {
            byte r = (byte)Math.Round(a.Red + (b.Red - a.Red) * t);
            byte g = (byte)Math.Round(a.Green + (b.Green - a.Green) * t);
            byte bl = (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * t);
            byte al = (byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * t);
            return new SKColor(r, g, bl, al);
        }

        private static SKColor InterpolatePalette((double Position, SKColor Color)[] palette, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            for (int i = 0; i < palette.Length - 1; i++)
            {
                var (p0, c0) = palette[i];
                var (p1, c1) = palette[i + 1];
                if (t >= p0 && t <= p1)
                {
                    double localT = (t - p0) / (p1 - p0);
                    return Lerp(c0, c1, localT);
                }
            }

            return palette[^1].Color;
        }

        private SKColor ColorForValue(double val, double min, double max)
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

        private SKColor GetConductivityColor(double workingVal, double normalized, double min, double max)
        {
            double norm = Math.Clamp(normalized, 0.0, 1.0);

            return _viewModel.SelectedConductivityDisplayMode switch
            {
                ConductivityDisplayMode.Classic => ColorForValue(workingVal, min, max),
                ConductivityDisplayMode.EnhancedDiverging => InterpolatePalette(EnhancedDivergingPalette, norm),
                ConductivityDisplayMode.Rainbow => SKColor.FromHsv((float)(240.0 * (1.0 - norm)), 90f, 100f),
                ConductivityDisplayMode.MatlabJet => InterpolatePalette(MatlabJetPalette, norm),
                ConductivityDisplayMode.Parula => InterpolatePalette(ParulaPalette, norm),
                ConductivityDisplayMode.Viridis => InterpolatePalette(ViridisPalette, norm),
                ConductivityDisplayMode.Plasma => InterpolatePalette(PlasmaPalette, norm),
                ConductivityDisplayMode.Magma => InterpolatePalette(MagmaPalette, norm),
                ConductivityDisplayMode.Cividis => InterpolatePalette(CividisPalette, norm),
                ConductivityDisplayMode.CoolWarm => InterpolatePalette(CoolWarmPalette, norm),
                _ => ColorForValue(workingVal, min, max)
            };
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

        private void OnColorBarPaintSurface(object? sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.Transparent);

            if (!_viewModel.HasMesh)
                return;

            var barRect = SKRect.Create(info.Width * 0.25f, 10, info.Width * 0.35f, info.Height - 30);
            const int steps = 96;
            var colors = new SKColor[steps];
            var positions = new float[steps];

            double min = _viewModel.MinCutoff;
            double max = _viewModel.MaxCutoff;
            if (max <= min)
                max = min + 1e-6;

            for (int i = 0; i < steps; i++)
            {
                double t = i / (double)(steps - 1);
                double value = min + (max - min) * t;
                var normalized = _viewModel.NormalizeValue(value);
                colors[i] = GetConductivityColor(normalized.Working, normalized.Normalized, normalized.Min, normalized.Max);
                positions[i] = (float)t;
            }

            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(barRect.Left, barRect.Bottom),
                new SKPoint(barRect.Left, barRect.Top),
                colors,
                positions,
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

                if (_viewModel.LbmGrid is LBMGrid lbm)
                {
                    DrawLbmGrid(canvas, new SKImageInfo(width, height), lbm);
                }
                else if (_viewModel.FemMesh is FEMMesh fem)
                {
                    DrawFemMesh(canvas, new SKImageInfo(width, height), fem);
                }
                else
                {
                    return;
                }

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
