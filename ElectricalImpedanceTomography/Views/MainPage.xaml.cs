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

        // Reusable paints to avoid repeated allocations during drawing
        private readonly SKPaint _lbmFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Black };
        private readonly SKPaint _lbmWall = new() { Style = SKPaintStyle.Fill, Color = SKColors.White };
        private readonly SKPaint _lbmElectrode = new() { Style = SKPaintStyle.Fill, Color = SKColors.Orange };
        private readonly SKPaint _lbmStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };

        private readonly SKPaint _femStroke = new() { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };
        private readonly SKPaint _femFill = new() { Style = SKPaintStyle.Fill };
        private readonly SKPaint _electrodeFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.Yellow };

        private float _scale, _marginX, _marginY, _minX, _minY, _meshWidth, _meshHeight;

        public MainPage()
        {
            InitializeComponent();

            _viewModel = Utility.Composition.Container.ResolveObject<MainPageViewModel>();

            BindingContext = _viewModel;

            _viewModel.DebugLog.CollectionChanged += OnDebugLogChanged;
            _viewModel.MeshUpdated += () => MeshCanvasView?.InvalidateSurface();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            var (startColor, endColor) = GetBackgroundPulseColors();
            this.StartBackgroundPulse(startColor, endColor);
            MeshCanvasView?.InvalidateSurface();
            if (ConsoleScroll != null && ConsoleStack != null)
                MainThread.BeginInvokeOnMainThread(async () =>
                    await ConsoleScroll.ScrollToAsync(0, ConsoleStack.Height, false));
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            this.StopBackgroundPulse();
        }

        private static (Color Start, Color End) GetBackgroundPulseColors()
        {
            var theme = Application.Current?.RequestedTheme ?? AppTheme.Light;
            return theme == AppTheme.Dark
                ? (Color.FromArgb("#101B2B"), Color.FromArgb("#1A2F45"))
                : (Color.FromArgb("#D7E4F8"), Color.FromArgb("#C8D6F2"));
        }

        private void OnLoadMeasurementClicked(object sender, EventArgs e)
        {
            _viewModel.OnLoadMeasurementClicked(sender, e);
        }

        private void OnLoadMeshClicked(object sender, EventArgs e)
        {
            _viewModel.OnLoadMeshClicked(sender, e);
            MeshCanvasView?.InvalidateSurface();
        }

        private void OnCanvasViewPaintSurface(object? sender, SkiaSharp.Views.Maui.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColor.Parse("#1C2638"));

            var discretization = Workspace.GetDiscretization();
            if (discretization is LBMGrid lbm)
                DrawLBMGrid(canvas, info, lbm);
            else if (discretization is FEMMesh fem)
                DrawFEMMesh(canvas, info, fem);
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

        private void DrawLBMGrid(SKCanvas canvas, SKImageInfo info, LBMGrid grid)
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

        private SKPoint ToCanvas(FEMVertex v)
            => new((float)(v.X - _minX) * _scale + _marginX,
                    MeshCanvasView.CanvasSize.Height - ((float)(v.Y - _minY) * _scale + _marginY));

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

            foreach (var v in mesh.Vertices.Where(v => v.IsElectrode))
                canvas.DrawCircle(ToCanvas(v), 4f, _electrodeFill);
        }

        private void OnConnectButtonClicked(object sender, EventArgs e)
        {

        }

        private void OnConsoleEntryCompleted(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_viewModel.ConsoleInput))
                _viewModel.SendConsoleMessageCommand.Execute(null);
        }

        private void OnDebugLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (ConsoleScroll == null || ConsoleStack == null)
                return;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await ConsoleScroll.ScrollToAsync(0, ConsoleStack.Height, false);
            });
        }

        private async void OnNavigationMenuTapped(object sender, TappedEventArgs e)
        {
            if (sender is VisualElement v)
            {
                await v.ScaleTo(0.95, 50);
                await v.ScaleTo(1.0, 50);
            }

            if (e.Parameter is string page)
            {
                _viewModel.NavigateCommand.Execute(page);
            }
        }
    }
}
