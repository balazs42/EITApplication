using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace ElectricalImpedanceTomography.Helpers
{
    public static class DistributionRenderingHelper
    {
        private static readonly SKColor DistributionCanvasBackgroundColor = SKColor.Parse("#1A2436");

        private readonly record struct FemTransform(float Scale,
                                                     float MarginX,
                                                     float MarginY,
                                                     float MinX,
                                                     float MinY,
                                                     float CanvasHeight);

        public static void DrawConductivity(SKCanvas canvas,
                                            SKImageInfo info,
                                            IDiscretization? discretization,
                                            ConductivityDistribution? distribution)
        {
            if (canvas == null)
                throw new ArgumentNullException(nameof(canvas));

            canvas.Clear(DistributionCanvasBackgroundColor);

            if (discretization == null || distribution == null)
                return;

            switch (discretization)
            {
                case FEMMesh fem:
                    DrawFemConductivity(canvas, info, fem, distribution);
                    break;
                case LBMGrid lbm:
                    DrawLbmConductivity(canvas, info, lbm, distribution);
                    break;
                default:
                    break;
            }
        }

        public static void DrawColorBar(SKCanvas canvas,
                                        SKImageInfo info,
                                        IDiscretization? discretization,
                                        ConductivityDistribution? distribution)
        {
            if (canvas == null)
                throw new ArgumentNullException(nameof(canvas));

            canvas.Clear(DistributionCanvasBackgroundColor);

            if (discretization == null || distribution == null)
                return;

            double min;
            double max;

            if (discretization is LBMGrid lbm)
            {
                (min, max) = GetLbmValueRange(lbm, distribution.Conductivities);
            }
            else
            {
                min = distribution.Conductivities.Values.Min();
                max = distribution.Conductivities.Values.Max();
                if (Math.Abs(max - min) < 1e-12)
                    max = min + 1e-12;
            }

            DrawColorBar(canvas, info, min, max);
        }

        private static void DrawFemConductivity(SKCanvas canvas,
                                                SKImageInfo info,
                                                FEMMesh mesh,
                                                ConductivityDistribution distribution)
        {
            var transform = ComputeFemTransform(mesh, info);
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };

            double min = distribution.Conductivities.Values.Min();
            double max = distribution.Conductivities.Values.Max();
            if (Math.Abs(max - min) < 1e-12)
                max = min + 1e-12;

            foreach (var element in mesh.GetElements().Cast<FEMElement>())
            {
                double value = distribution.GetConductivity(element.Id);
                fill.Color = ColorForValue(value, min, max);

                using var path = new SKPath();
                path.MoveTo(ToCanvas(element.Vertices[0], transform));
                path.LineTo(ToCanvas(element.Vertices[1], transform));
                path.LineTo(ToCanvas(element.Vertices[2], transform));
                path.Close();

                canvas.DrawPath(path, fill);
                canvas.DrawPath(path, stroke);
            }
        }

        private static void DrawLbmConductivity(SKCanvas canvas,
                                                SKImageInfo info,
                                                LBMGrid grid,
                                                ConductivityDistribution distribution)
        {
            float cellWidth = info.Width / (float)grid.Nx;
            float cellHeight = info.Height / (float)grid.Ny;
            var (min, max) = GetLbmValueRange(grid, distribution.Conductivities);

            for (int y = 0; y < grid.Ny; y++)
            {
                for (int x = 0; x < grid.Nx; x++)
                {
                    var element = grid.GetElementAt(x, y);
                    SKColor color;

                    if (element.GhostElement)
                    {
                        color = SKColors.DarkGray;
                    }
                    else if (element.IsWall)
                    {
                        color = SKColors.Black;
                    }
                    else
                    {
                        double value = distribution.Conductivities.TryGetValue(element.Id, out var v) ? v : min;
                        color = ColorForValue(value, min, max);
                    }

                    using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = color };
                    var rect = SKRect.Create(x * cellWidth, y * cellHeight, cellWidth, cellHeight);
                    canvas.DrawRect(rect, fill);
                    canvas.DrawRect(rect, new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 });
                }
            }
        }

        private static FemTransform ComputeFemTransform(FEMMesh mesh, SKImageInfo info)
        {
            const float padding = 10f;
            float availableWidth = info.Width - 2 * padding;
            float availableHeight = info.Height - 2 * padding;

            float minX = (float)mesh.Vertices.Min(v => v.X);
            float minY = (float)mesh.Vertices.Min(v => v.Y);
            float maxX = (float)mesh.Vertices.Max(v => v.X);
            float maxY = (float)mesh.Vertices.Max(v => v.Y);

            float meshWidth = maxX - minX;
            float meshHeight = maxY - minY;
            float scale = Math.Min(availableWidth / meshWidth, availableHeight / meshHeight);

            float usedWidth = meshWidth * scale;
            float usedHeight = meshHeight * scale;
            float marginX = padding + (availableWidth - usedWidth) / 2f;
            float marginY = padding + (availableHeight - usedHeight) / 2f;

            return new FemTransform(scale, marginX, marginY, minX, minY, info.Height);
        }

        private static SKPoint ToCanvas(FEMVertex vertex, in FemTransform transform)
        {
            float x = (float)(vertex.X - transform.MinX) * transform.Scale + transform.MarginX;
            float y = transform.CanvasHeight - ((float)(vertex.Y - transform.MinY) * transform.Scale + transform.MarginY);
            return new SKPoint(x, y);
        }

        private static SKColor ColorForValue(double value, double min, double max)
        {
            double midpoint = (min + max) * 0.5;
            if (value >= midpoint)
            {
                float t = (float)((value - midpoint) / (max - midpoint));
                t = Math.Clamp(t, 0f, 1f);
                byte r = (byte)(255 * t);
                return new SKColor(r, 0, 0);
            }
            else
            {
                float t = (float)((midpoint - value) / (midpoint - min));
                t = Math.Clamp(t, 0f, 1f);
                byte b = (byte)(255 * t);
                return new SKColor(0, 0, b);
            }
        }

        private static (double Min, double Max) GetLbmValueRange(LBMGrid grid, IReadOnlyDictionary<int, double> values)
        {
            bool hasValue = false;
            double min = 0.0;
            double max = 0.0;

            foreach (var element in grid.GetElements().Cast<LBMElement>())
            {
                if (element.IsWall)
                    continue;

                if (!values.TryGetValue(element.Id, out double value))
                    continue;

                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;

                if (!hasValue)
                {
                    min = max = value;
                    hasValue = true;
                }
                else
                {
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }

            if (!hasValue)
                return (0.0, 1e-12);

            if (Math.Abs(max - min) < 1e-12)
                max = min + 1e-12;

            return (min, max);
        }

        private static void DrawColorBar(SKCanvas canvas, SKImageInfo info, double min, double max)
        {
            var rect = new SKRect(0, 0, info.Width, info.Height);
            int steps = 256;
            var colors = new SKColor[steps];
            var positions = new float[steps];

            for (int i = 0; i < steps; i++)
            {
                double t = i / (double)(steps - 1);
                double value = min + (max - min) * t;
                colors[i] = ColorForValue(value, min, max);
                positions[i] = (float)t;
            }

            using var shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top),
                                                              new SKPoint(rect.Right, rect.Top),
                                                              colors,
                                                              positions,
                                                              SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(rect, paint);

            using var border = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Black,
                StrokeWidth = 1,
                IsAntialias = true
            };
            canvas.DrawRect(rect, border);
        }
    }
}
