using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui.Controls;
using Utility.Classes.Application;
using Utility.Classes.Meshing.FiniteElementMesh;
using System.Linq;

namespace ElectricalImpedanceTomography.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainPageViewModel _viewModel;

        public MainPage()
        {
            InitializeComponent();

            _viewModel = Utility.Composition.Container.ResolveObject<MainPageViewModel>();

            BindingContext = _viewModel;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            MeshCanvasView?.InvalidateSurface();
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
            canvas.Clear(SKColors.White);

            var mesh = Workspace.GetMesh();
            if (mesh is FEMMesh femMesh)
            {
                DrawFEMMesh(canvas, info.Width, info.Height, femMesh);

    }
            else
            {
                DrawCheckerboard(canvas, info.Width, info.Height);
            }
        }

        private static void DrawCheckerboard(SKCanvas canvas, int width, int height)
        {
            const int size = 20;
            using var darkPaint = new SKPaint { Color = SKColors.Gray };
            using var lightPaint = new SKPaint { Color = SKColors.Black };
            for (int y = 0; y < height; y += size)
            {
                for (int x = 0; x < width; x += size)
                {
                    var paint = ((x / size + y / size) % 2 == 0) ? darkPaint : lightPaint;
                    canvas.DrawRect(x, y, size, size, paint);
                }
            }
        }

        private static void DrawFEMMesh(SKCanvas canvas, int width, int height, FEMMesh mesh)
        {
            if (mesh.Vertices.Count == 0)
            {
                DrawCheckerboard(canvas, width, height);
                return;
            }

            var minX = mesh.Vertices.Min(v => v.X);
            var maxX = mesh.Vertices.Max(v => v.X);
            var minY = mesh.Vertices.Min(v => v.Y);
            var maxY = mesh.Vertices.Max(v => v.Y);

            var scaleX = width / (float)(maxX - minX);
            var scaleY = height / (float)(maxY - minY);
            var scale = Math.Min(scaleX, scaleY);

            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 1, Style = SKPaintStyle.Stroke };

            foreach (var element in mesh.ElementsTyped)
            {
                var v1 = element.Vertices[0];
                var v2 = element.Vertices[1];
                var v3 = element.Vertices[2];

                var path = new SKPath();
                path.MoveTo((float)((v1.X - minX) * scale), height - (float)((v1.Y - minY) * scale));
                path.LineTo((float)((v2.X - minX) * scale), height - (float)((v2.Y - minY) * scale));
                path.LineTo((float)((v3.X - minX) * scale), height - (float)((v3.Y - minY) * scale));
                path.Close();
                canvas.DrawPath(path, paint);
            }
        }
    }
}
