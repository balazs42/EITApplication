using ElectricalImpedanceTomography.ViewModels;
using ElectricalImpedanceTomography.Controls;
using ElectricalImpedanceTomography.Extensions;
using Microsoft.Maui.Graphics;
using SkiaSharp;
using System.Collections.Specialized;
using Utility.Classes.Application;
using Utility.Rendering;

namespace ElectricalImpedanceTomography.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainPageViewModel _viewModel;

        private readonly DiscretizationCanvasRenderer _meshRenderer = new();

        // Paints for HEADER Text
        private readonly SKPaint _textPaint = new()
        {
            TextSize = 80,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
            TextAlign = SKTextAlign.Center
        };

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

            string text = "IMPALA";
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
            var discretization = Workspace.GetDiscretization();
            _meshRenderer.Draw(
                e.Surface.Canvas,
                e.Info,
                new DiscretizationRenderRequest(discretization, DiscretizationRenderMode.Geometry),
                new DiscretizationCanvasRenderOptions
                {
                    BackgroundColor = SKColors.Transparent,
                    ConductivityDisplayMode = ConductivityDisplayMode.Classic,
                    FemStrokeWidth = 0.5f,
                    LbmDefaultColor = SKColors.Black,
                    LbmWallColor = SKColors.White,
                    LbmElectrodeColor = SKColors.Orange,
                    LbmStrokeColor = SKColors.LightGray,
                    ElectrodePointColor = SKColors.Yellow,
                    ElectrodeSegmentColor = SKColors.Gold
                });
        }
    }
}
