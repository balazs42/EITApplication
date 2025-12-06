using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using System.Linq;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Rendering;

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
                _fillPaint.Color = GetHeatColor(value, _viewModel.SelectedConductivityDisplayMode);

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
                _fillPaint.Color = GetHeatColor(value, _viewModel.SelectedConductivityDisplayMode);

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

        private SKColor GetHeatColor(double norm, ConductivityDisplayMode mode)
        {
            norm = Math.Clamp(norm, 0, 1);
            return mode switch
            {
                ConductivityDisplayMode.Classic => ColorForValue(norm),
                ConductivityDisplayMode.EnhancedDiverging => InterpolatePalette(EnhancedDivergingPalette, norm),
                ConductivityDisplayMode.Rainbow => SKColor.FromHsv((float)(norm * 360f), 100f, 100f),
                ConductivityDisplayMode.MatlabJet => InterpolatePalette(MatlabJetPalette, norm),
                ConductivityDisplayMode.Parula => InterpolatePalette(ParulaPalette, norm),
                ConductivityDisplayMode.Viridis => InterpolatePalette(ViridisPalette, norm),
                ConductivityDisplayMode.Plasma => InterpolatePalette(PlasmaPalette, norm),
                ConductivityDisplayMode.Magma => InterpolatePalette(MagmaPalette, norm),
                ConductivityDisplayMode.Cividis => InterpolatePalette(CividisPalette, norm),
                ConductivityDisplayMode.CoolWarm => InterpolatePalette(CoolWarmPalette, norm),
                _ => ColorForValue(norm)
            };
        }

        private static SKColor ColorForValue(double norm)
        {
            byte r = (byte)(255 * norm);
            byte b = (byte)(255 * (1 - norm));
            return new SKColor(r, 0, b);
        }

        private static SKColor InterpolatePalette((double Position, SKColor Color)[] palette, double t)
        {
            if (t <= palette[0].Position)
                return palette[0].Color;
            for (int i = 0; i < palette.Length - 1; i++)
            {
                var (posA, colorA) = palette[i];
                var (posB, colorB) = palette[i + 1];
                if (t >= posA && t <= posB)
                {
                    double range = posB - posA;
                    double localT = range <= 0 ? 0 : (t - posA) / range;
                    return Lerp(colorA, colorB, localT);
                }
            }

            return palette[^1].Color;
        }

        private static SKColor Lerp(SKColor a, SKColor b, double t)
        {
            byte r = (byte)(a.Red + (b.Red - a.Red) * t);
            byte g = (byte)(a.Green + (b.Green - a.Green) * t);
            byte bl = (byte)(a.Blue + (b.Blue - a.Blue) * t);
            byte al = (byte)(a.Alpha + (b.Alpha - a.Alpha) * t);
            return new SKColor(r, g, bl, al);
        }
    }
}