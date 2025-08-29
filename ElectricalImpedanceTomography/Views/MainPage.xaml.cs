using ElectricalImpedanceTomography.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui.Controls;
using Utility.Classes.Application;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
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
            if (mesh is LBMMesh lbm)
            {
                DrawLBMMesh(canvas, info, lbm);
            }
            else if (mesh is FEMMesh fem)
            {
                DrawFEMMesh(canvas, info, fem);
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

        private static void DrawLBMMesh(SKCanvas canvas, SKImageInfo info, LBMMesh mesh)
        {
            float cellW = (float)info.Width / mesh.Nx;
            float cellH = (float)info.Height / mesh.Ny;
            var elems = mesh.ElementsTyped;
            double min = elems.Min(el => el.Conductivity);
            double max = elems.Max(el => el.Conductivity);
            var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray, StrokeWidth = 1 };
            var wall = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black };
            var electrode = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Orange };

            for (int y = 0; y < mesh.Ny; y++)
            {
                for (int x = 0; x < mesh.Nx; x++)
                {
                    var el = mesh.GetElementAt(x, y);
                    SKPaint fill;
                    if (el.IsElectrode)
                        fill = electrode;
                    else if (el.IsWall)
                        fill = wall;
                    else
                        fill = new SKPaint { Style = SKPaintStyle.Fill, Color = ColorForValue(el.Conductivity, min, max) };
                    var r = SKRect.Create(x * cellW, y * cellH, cellW, cellH);
                    canvas.DrawRect(r, fill);
                    canvas.DrawRect(r, stroke);
                }
            }
        }

        private static void DrawFEMMesh(SKCanvas canvas, SKImageInfo info, FEMMesh mesh)
        {
            const float pad = 10f;
            float availW = info.Width - 2 * pad;
            float availH = info.Height - 2 * pad;
            var verts = mesh.Vertices;
            float minX = (float)verts.Min(v => v.X);
            float minY = (float)verts.Min(v => v.Y);
            float maxX = (float)verts.Max(v => v.X);
            float maxY = (float)verts.Max(v => v.Y);
            float meshW = maxX - minX;
            float meshH = maxY - minY;
            float scale = Math.Min(availW / meshW, availH / meshH);
            float usedW = meshW * scale;
            float usedH = meshH * scale;
            float marginX = pad + (availW - usedW) / 2f;
            float marginY = pad + (availH - usedH) / 2f;

            SKPoint ToCanvas(FEMVertex v)
                => new SKPoint((float)(v.X - minX) * scale + marginX,
                               info.Height - ((float)(v.Y - minY) * scale + marginY));

            var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };
            var elements = mesh.ElementsTyped;
            double min = elements.Min(el => el.Conductivity);
            double max = elements.Max(el => el.Conductivity);

            foreach (var el in elements)
            {
                var p1 = ToCanvas(el.Vertices[0]);
                var p2 = ToCanvas(el.Vertices[1]);
                var p3 = ToCanvas(el.Vertices[2]);
                using var path = new SKPath();
                path.MoveTo(p1); path.LineTo(p2); path.LineTo(p3); path.Close();
                using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = ColorForValue(el.Conductivity, min, max) };
                canvas.DrawPath(path, fill);
                canvas.DrawPath(path, stroke);
            }

            using var electrodeFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Yellow };
            foreach (var v in mesh.Vertices.Where(v => v.IsElectrode))
                canvas.DrawCircle(ToCanvas(v), 4f, electrodeFill);
        }

        private void OnConnectButtonClicked(object sender, EventArgs e)
        {
            
        }
    }
}
