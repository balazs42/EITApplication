using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Rendering;

namespace ElectricalImpedanceTomography.Controls;

public enum DiscretizationRenderMode
{
    Geometry,
    Conductivity,
    Potential
}

public enum ColorBarOrientation
{
    Horizontal,
    Vertical
}

public sealed record DiscretizationRenderRequest(
    IDiscretization? Discretization,
    DiscretizationRenderMode Mode = DiscretizationRenderMode.Geometry,
    ConductivityDistribution? ConductivityDistribution = null,
    PotentialDistribution? PotentialDistribution = null);

public sealed class DiscretizationCanvasRenderOptions
{
    public SKColor BackgroundColor { get; init; } = SKColor.Parse("#1A2436");
    public ConductivityDisplayMode ConductivityDisplayMode { get; init; } = ConductivityDisplayMode.Classic;
    public PotentialDisplayMode PotentialDisplayMode { get; init; } = PotentialDisplayMode.Default;
    public float FemPadding { get; init; } = 10f;
    public bool ShowWireframe { get; init; } = true;
    public bool ShowElectrodePoints { get; init; } = true;
    public bool ShowElectrodeSegments { get; init; } = true;
    public bool ShowGhostOverlay { get; init; } = true;
    public bool ShowGuideGrid { get; init; }
    public float GuideGridSize { get; init; } = 40f;
    public int GuideGridMajorEvery { get; init; } = 5;
    public SKColor GuideGridMajorColor { get; init; } = SKColor.Parse("#253045");
    public SKColor GuideGridMinorColor { get; init; } = SKColor.Parse("#1A2332");
    public bool UseLogScale { get; init; }
    public double? MinimumValueOverride { get; init; }
    public double? MaximumValueOverride { get; init; }
    public int VisualReferenceNodeId { get; init; } = 1;
    public IReadOnlyCollection<int>? HighlightedElementIds { get; init; }
    public SKColor HighlightColor { get; init; } = SKColors.LimeGreen;
    public SKColor FemStrokeColor { get; init; } = SKColors.Black;
    public float FemStrokeWidth { get; init; } = 1f;
    public SKColor LbmStrokeColor { get; init; } = SKColors.Black;
    public float LbmStrokeWidth { get; init; } = 1f;
    public SKColor LbmDefaultColor { get; init; } = SKColors.White;
    public SKColor LbmWallColor { get; init; } = SKColors.Black;
    public SKColor LbmGhostColor { get; init; } = SKColor.Parse("#546E7A");
    public SKColor LbmGhostOverlayColor { get; init; } = SKColors.WhiteSmoke;
    public SKColor LbmElectrodeColor { get; init; } = SKColors.Orange;
    public SKColor VirtualElectrodeColor { get; init; } = SKColor.Parse("#AA00FF");
    public SKColor ElectrodePointColor { get; init; } = SKColors.Yellow;
    public float ElectrodePointRadius { get; init; } = 4f;
    public SKColor ElectrodeSegmentColor { get; init; } = SKColors.Gold;
    public float ElectrodeSegmentWidth { get; init; } = 3f;
}

public sealed class DiscretizationColorBarOptions
{
    public SKColor BackgroundColor { get; init; } = SKColor.Parse("#1A2436");
    public SKColor BorderColor { get; init; } = SKColors.Black;
    public SKColor TextColor { get; init; } = SKColors.White;
    public float TextSize { get; init; } = 12f;
    public ColorBarOrientation Orientation { get; init; } = ColorBarOrientation.Horizontal;
    public bool ShowMidpointLabel { get; init; }
}

public readonly record struct FemViewport(float Scale,
                                          float MarginX,
                                          float MarginY,
                                          float MinX,
                                          float MinY,
                                          float CanvasHeight);

public readonly record struct LbmViewport(float CellWidth, float CellHeight);

public sealed class DiscretizationCanvasRenderer
{
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

    public void Draw(SKCanvas canvas,
                     SKImageInfo info,
                     DiscretizationRenderRequest request,
                     DiscretizationCanvasRenderOptions? options = null)
    {
        if (canvas == null)
            throw new ArgumentNullException(nameof(canvas));

        options ??= new DiscretizationCanvasRenderOptions();

        canvas.Clear(options.BackgroundColor);

        if (options.ShowGuideGrid)
            DrawGuideGrid(canvas, info, options);

        if (request.Discretization == null)
            return;

        switch (request.Discretization)
        {
            case FEMMesh fem:
                DrawFem(canvas, info, fem, request, options);
                break;
            case LBMGrid lbm:
                DrawLbm(canvas, info, lbm, request, options);
                break;
        }
    }

    public void DrawColorBar(SKCanvas canvas,
                             SKImageInfo info,
                             DiscretizationRenderRequest request,
                             DiscretizationCanvasRenderOptions? renderOptions = null,
                             DiscretizationColorBarOptions? colorBarOptions = null)
    {
        if (canvas == null)
            throw new ArgumentNullException(nameof(canvas));

        renderOptions ??= new DiscretizationCanvasRenderOptions();
        colorBarOptions ??= new DiscretizationColorBarOptions();

        canvas.Clear(colorBarOptions.BackgroundColor);

        if (request.Discretization == null)
            return;

        if (!TryGetRange(request, renderOptions, out var min, out var max))
            return;

        DrawColorBarCore(canvas, info, request.Mode, min, max, renderOptions, colorBarOptions);
    }

    public static FemViewport ComputeFemViewport(FEMMesh mesh, SKImageInfo info, float padding = 10f)
    {
        float availableWidth = info.Width - 2 * padding;
        float availableHeight = info.Height - 2 * padding;

        float minX = (float)mesh.Vertices.Min(v => v.X);
        float minY = (float)mesh.Vertices.Min(v => v.Y);
        float maxX = (float)mesh.Vertices.Max(v => v.X);
        float maxY = (float)mesh.Vertices.Max(v => v.Y);

        float meshWidth = Math.Max(1e-6f, maxX - minX);
        float meshHeight = Math.Max(1e-6f, maxY - minY);
        float scale = Math.Min(availableWidth / meshWidth, availableHeight / meshHeight);
        float usedWidth = meshWidth * scale;
        float usedHeight = meshHeight * scale;
        float marginX = padding + (availableWidth - usedWidth) / 2f;
        float marginY = padding + (availableHeight - usedHeight) / 2f;

        return new FemViewport(scale, marginX, marginY, minX, minY, info.Height);
    }

    public static SKPoint ToCanvas(FEMVertex vertex, in FemViewport viewport)
    {
        float x = (float)(vertex.X - viewport.MinX) * viewport.Scale + viewport.MarginX;
        float y = viewport.CanvasHeight - ((float)(vertex.Y - viewport.MinY) * viewport.Scale + viewport.MarginY);
        return new SKPoint(x, y);
    }

    public static LbmViewport ComputeLbmViewport(LBMGrid grid, SKImageInfo info)
        => new(info.Width / (float)grid.Nx, info.Height / (float)grid.Ny);

    private void DrawFem(SKCanvas canvas,
                         SKImageInfo info,
                         FEMMesh mesh,
                         DiscretizationRenderRequest request,
                         DiscretizationCanvasRenderOptions options)
    {
        var viewport = ComputeFemViewport(mesh, info, options.FemPadding);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = options.FemStrokeColor,
            StrokeWidth = options.FemStrokeWidth,
            IsAntialias = true
        };

        TryGetRange(request, options, out var min, out var max);

        foreach (var element in mesh.ElementsTyped)
        {
            double value = request.Mode switch
            {
                DiscretizationRenderMode.Potential => GetFemPotentialValue(element, request.PotentialDistribution, options.VisualReferenceNodeId),
                _ => GetFemConductivityValue(element, request.ConductivityDistribution)
            };

            fill.Color = request.Mode == DiscretizationRenderMode.Potential
                ? GetPotentialColor(value, min, max, options.PotentialDisplayMode)
                : GetConductivityColor(value, min, max, options);

            using var path = new SKPath();
            path.MoveTo(ToCanvas(element.Vertices[0], viewport));
            path.LineTo(ToCanvas(element.Vertices[1], viewport));
            path.LineTo(ToCanvas(element.Vertices[2], viewport));
            path.Close();

            canvas.DrawPath(path, fill);
            if (options.ShowWireframe)
                canvas.DrawPath(path, stroke);
        }

        DrawFemElectrodes(canvas, mesh, viewport, options);
    }

    private void DrawLbm(SKCanvas canvas,
                         SKImageInfo info,
                         LBMGrid grid,
                         DiscretizationRenderRequest request,
                         DiscretizationCanvasRenderOptions options)
    {
        var viewport = ComputeLbmViewport(grid, info);
        TryGetRange(request, options, out var min, out var max);
        var highlighted = options.HighlightedElementIds;
        var electrodeLookup = grid.ElectrodesTyped.Cast<LBMElectrode>()
            .ToDictionary(e => e.Id, e => e.IsVirtual);

        using var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = options.LbmStrokeColor,
            StrokeWidth = options.LbmStrokeWidth,
            IsAntialias = true
        };

        using var ghostOverlay = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = options.LbmGhostOverlayColor,
            StrokeWidth = 0.75f,
            IsAntialias = true
        };

        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };

        for (int y = 0; y < grid.Ny; y++)
        {
            for (int x = 0; x < grid.Nx; x++)
            {
                var element = grid.GetElementAt(x, y);
                var rect = SKRect.Create(x * viewport.CellWidth, y * viewport.CellHeight, viewport.CellWidth, viewport.CellHeight);
                fill.Color = ResolveLbmColor(element, request, options, electrodeLookup, highlighted, min, max);
                canvas.DrawRect(rect, fill);
                if (options.ShowWireframe)
                    canvas.DrawRect(rect, stroke);

                if (options.ShowGhostOverlay && element.GhostElement)
                {
                    canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, ghostOverlay);
                    canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Top, ghostOverlay);
                }
            }
        }
    }

    private void DrawFemElectrodes(SKCanvas canvas, FEMMesh mesh, in FemViewport viewport, DiscretizationCanvasRenderOptions options)
    {
        if (options.ShowElectrodeSegments)
        {
            using var segmentPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = options.ElectrodeSegmentColor,
                StrokeWidth = options.ElectrodeSegmentWidth,
                IsAntialias = true
            };

            foreach (var segment in mesh.GetElectrodeSegments())
            {
                var start = ToCanvas(segment.Start, viewport);
                var end = ToCanvas(segment.End, viewport);
                canvas.DrawLine(start, end, segmentPaint);
            }
        }

        if (!options.ShowElectrodePoints)
            return;

        var electrodeLookup = mesh.ElectrodesTyped.ToDictionary(e => e.Id, e => e.IsVirtual);
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        foreach (var vertex in mesh.Vertices.Where(v => v.IsElectrode))
        {
            bool isVirtual = vertex.ElectrodeId >= 0
                             && electrodeLookup.TryGetValue(vertex.ElectrodeId, out bool value)
                             && value;
            paint.Color = isVirtual ? options.VirtualElectrodeColor : options.ElectrodePointColor;
            canvas.DrawCircle(ToCanvas(vertex, viewport), options.ElectrodePointRadius, paint);
        }
    }

    private SKColor ResolveLbmColor(LBMElement element,
                                    DiscretizationRenderRequest request,
                                    DiscretizationCanvasRenderOptions options,
                                    IReadOnlyDictionary<int, bool> electrodeLookup,
                                    IReadOnlyCollection<int>? highlighted,
                                    double min,
                                    double max)
    {
        if (highlighted != null && highlighted.Contains(element.Id))
            return options.HighlightColor;

        if (element.IsElectrode)
        {
            bool isVirtual = element.ElectrodeId >= 0
                             && electrodeLookup.TryGetValue(element.ElectrodeId, out bool value)
                             && value;
            return isVirtual ? options.VirtualElectrodeColor : options.LbmElectrodeColor;
        }

        if (element.GhostElement)
            return options.LbmGhostColor;

        if (element.IsWall)
            return options.LbmWallColor;

        return request.Mode switch
        {
            DiscretizationRenderMode.Potential => GetPotentialColor(GetLbmPotentialValue(element, request.PotentialDistribution),
                                                                    min,
                                                                    max,
                                                                    options.PotentialDisplayMode),
            DiscretizationRenderMode.Conductivity => GetConductivityColor(GetLbmConductivityValue(element, request.ConductivityDistribution),
                                                                          min,
                                                                          max,
                                                                          options),
            _ => ResolveGeometryColor(element, request.ConductivityDistribution, min, max, options)
        };
    }

    private static SKColor ResolveGeometryColor(LBMElement element,
                                                ConductivityDistribution? distribution,
                                                double min,
                                                double max,
                                                DiscretizationCanvasRenderOptions options)
    {
        double value = distribution?.Conductivities.TryGetValue(element.Id, out var explicitValue) == true
            ? explicitValue
            : element.Conductivity;

        if (distribution == null && Math.Abs(value - 1.0) <= 1e-6)
            return options.LbmDefaultColor;

        return GetConductivityColor(value, min, max, options);
    }

    private bool TryGetRange(DiscretizationRenderRequest request,
                             DiscretizationCanvasRenderOptions options,
                             out double min,
                             out double max)
    {
        min = 0.0;
        max = 1e-12;

        if (request.Discretization == null)
            return false;

        return request.Mode switch
        {
            DiscretizationRenderMode.Potential => TryGetPotentialRange(request, options, out min, out max),
            _ => TryGetConductivityRange(request, options, out min, out max)
        };
    }

    private static bool TryGetPotentialRange(DiscretizationRenderRequest request,
                                             DiscretizationCanvasRenderOptions options,
                                             out double min,
                                             out double max)
    {
        min = 0.0;
        max = 1e-12;
        if (request.PotentialDistribution == null || request.Discretization == null)
            return false;

        IEnumerable<double> values = request.Discretization switch
        {
            FEMMesh => request.PotentialDistribution.Potentials.Values
                .Select(v => v - GetFemReferencePotential(request.PotentialDistribution, options.VisualReferenceNodeId)),
            LBMGrid => request.PotentialDistribution.Potentials.Values,
            _ => Array.Empty<double>()
        };

        return TryNormalizeRange(values, out min, out max);
    }

    private static bool TryGetConductivityRange(DiscretizationRenderRequest request,
                                                DiscretizationCanvasRenderOptions options,
                                                out double min,
                                                out double max)
    {
        min = options.MinimumValueOverride ?? 0.0;
        max = options.MaximumValueOverride ?? 1e-12;

        if (request.Discretization == null)
            return false;

        var values = EnumerateConductivityValues(request)
            .Select(v => ApplyConductivityTransform(v, options))
            .ToList();

        if (values.Count == 0)
            return false;

        if (!options.MinimumValueOverride.HasValue)
            min = values.Min();
        if (!options.MaximumValueOverride.HasValue)
            max = values.Max();

        if (Math.Abs(max - min) < 1e-12)
            max = min + 1e-12;

        return true;
    }

    private static IEnumerable<double> EnumerateConductivityValues(DiscretizationRenderRequest request)
    {
        if (request.Discretization is FEMMesh fem)
        {
            foreach (var element in fem.ElementsTyped)
                yield return GetFemConductivityValue(element, request.ConductivityDistribution);
            yield break;
        }

        if (request.Discretization is not LBMGrid lbm)
            yield break;

        foreach (var element in lbm.ElementsTyped)
        {
            if (element.IsWall)
                continue;

            yield return GetLbmConductivityValue(element, request.ConductivityDistribution);
        }
    }

    private static double GetFemConductivityValue(FEMElement element, ConductivityDistribution? distribution)
        => distribution?.Conductivities.TryGetValue(element.Id, out var value) == true ? value : element.Conductivity;

    private static double GetLbmConductivityValue(LBMElement element, ConductivityDistribution? distribution)
        => distribution?.Conductivities.TryGetValue(element.Id, out var value) == true ? value : element.Conductivity;

    private static double GetFemPotentialValue(FEMElement element,
                                               PotentialDistribution? distribution,
                                               int visualReferenceNodeId)
    {
        if (distribution == null)
            return 0.0;

        double reference = GetFemReferencePotential(distribution, visualReferenceNodeId);
        double average = element.Vertices.Average(vertex => distribution.GetPotential(vertex.GlobalId));
        return average - reference;
    }

    private static double GetLbmPotentialValue(LBMElement element, PotentialDistribution? distribution)
        => distribution?.Potentials.TryGetValue(element.Id, out var value) == true ? value : 0.0;

    private static double GetFemReferencePotential(PotentialDistribution distribution, int visualReferenceNodeId)
        => distribution.Potentials.TryGetValue(visualReferenceNodeId, out var rawReference) ? rawReference : 0.0;

    private static double ApplyConductivityTransform(double value, DiscretizationCanvasRenderOptions options)
        => options.UseLogScale ? Math.Log10(Math.Max(1e-6, value) + 1.0) : value;

    private static SKColor GetConductivityColor(double rawValue,
                                                double min,
                                                double max,
                                                DiscretizationCanvasRenderOptions options)
    {
        double workingValue = ApplyConductivityTransform(rawValue, options);
        workingValue = Math.Clamp(workingValue, min, max);
        double normalized = (workingValue - min) / (max - min);
        normalized = double.IsNaN(normalized) ? 0.0 : Math.Clamp(normalized, 0.0, 1.0);

        return options.ConductivityDisplayMode switch
        {
            ConductivityDisplayMode.Classic => ColorForValue(workingValue, min, max),
            ConductivityDisplayMode.EnhancedDiverging => InterpolatePalette(EnhancedDivergingPalette, normalized),
            ConductivityDisplayMode.Rainbow => SKColor.FromHsv((float)(240.0 * (1.0 - normalized)), 90f, 100f),
            ConductivityDisplayMode.MatlabJet => InterpolatePalette(MatlabJetPalette, normalized),
            ConductivityDisplayMode.Parula => InterpolatePalette(ParulaPalette, normalized),
            ConductivityDisplayMode.Viridis => InterpolatePalette(ViridisPalette, normalized),
            ConductivityDisplayMode.Plasma => InterpolatePalette(PlasmaPalette, normalized),
            ConductivityDisplayMode.Magma => InterpolatePalette(MagmaPalette, normalized),
            ConductivityDisplayMode.Cividis => InterpolatePalette(CividisPalette, normalized),
            ConductivityDisplayMode.CoolWarm => InterpolatePalette(CoolWarmPalette, normalized),
            _ => ColorForValue(workingValue, min, max)
        };
    }

    private static SKColor GetPotentialColor(double value,
                                             double min,
                                             double max,
                                             PotentialDisplayMode displayMode)
    {
        float normalized = (float)((value - min) / (max - min));
        normalized = Math.Clamp(normalized, 0f, 1f);
        return displayMode switch
        {
            PotentialDisplayMode.Grayscale => new SKColor((byte)(normalized * 255), (byte)(normalized * 255), (byte)(normalized * 255)),
            PotentialDisplayMode.Inverted =>
                new SKColor((byte)(255 - ColorForValue(value, min, max).Red),
                            (byte)(255 - ColorForValue(value, min, max).Green),
                            (byte)(255 - ColorForValue(value, min, max).Blue)),
            PotentialDisplayMode.Heatmap => new SKColor(255, (byte)(255 * (1 - normalized)), 0),
            PotentialDisplayMode.Rainbow => SKColor.FromHsv(normalized * 360f, 100f, 100f),
            _ => ColorForValue(value, min, max)
        };
    }

    private static SKColor ColorForValue(double value, double min, double max)
    {
        double midpoint = (min + max) * 0.5;
        if (value >= midpoint)
        {
            float t = (float)((value - midpoint) / (max - midpoint));
            t = Math.Clamp(t, 0f, 1f);
            byte red = (byte)(255 * t);
            return new SKColor(red, 0, 0);
        }

        float blueT = (float)((midpoint - value) / (midpoint - min));
        blueT = Math.Clamp(blueT, 0f, 1f);
        byte blue = (byte)(255 * blueT);
        return new SKColor(0, 0, blue);
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

    private static SKColor Lerp(SKColor from, SKColor to, double t)
    {
        byte red = (byte)Math.Round(from.Red + (to.Red - from.Red) * t);
        byte green = (byte)Math.Round(from.Green + (to.Green - from.Green) * t);
        byte blue = (byte)Math.Round(from.Blue + (to.Blue - from.Blue) * t);
        byte alpha = (byte)Math.Round(from.Alpha + (to.Alpha - from.Alpha) * t);
        return new SKColor(red, green, blue, alpha);
    }

    private static bool TryNormalizeRange(IEnumerable<double> values, out double min, out double max)
    {
        min = double.PositiveInfinity;
        max = double.NegativeInfinity;
        foreach (var value in values)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                continue;

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        if (double.IsInfinity(min) || double.IsInfinity(max))
        {
            min = 0.0;
            max = 1e-12;
            return false;
        }

        if (Math.Abs(max - min) < 1e-12)
            max = min + 1e-12;

        return true;
    }

    private static void DrawGuideGrid(SKCanvas canvas, SKImageInfo info, DiscretizationCanvasRenderOptions options)
    {
        using var majorPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = options.GuideGridMajorColor,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        using var minorPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = options.GuideGridMinorColor,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        float gridSize = Math.Max(4f, options.GuideGridSize);
        int majorEvery = Math.Max(1, options.GuideGridMajorEvery);

        int xIndex = 0;
        for (float x = 0; x < info.Width; x += gridSize, xIndex++)
            canvas.DrawLine(x, 0, x, info.Height, xIndex % majorEvery == 0 ? majorPaint : minorPaint);

        int yIndex = 0;
        for (float y = 0; y < info.Height; y += gridSize, yIndex++)
            canvas.DrawLine(0, y, info.Width, y, yIndex % majorEvery == 0 ? majorPaint : minorPaint);
    }

    private static void DrawColorBarCore(SKCanvas canvas,
                                         SKImageInfo info,
                                         DiscretizationRenderMode mode,
                                         double min,
                                         double max,
                                         DiscretizationCanvasRenderOptions renderOptions,
                                         DiscretizationColorBarOptions colorBarOptions)
    {
        int steps = 128;
        var colors = new SKColor[steps];
        var positions = new float[steps];
        for (int i = 0; i < steps; i++)
        {
            double t = i / (double)(steps - 1);
            double value = min + (max - min) * t;
            colors[i] = mode == DiscretizationRenderMode.Potential
                ? GetPotentialColor(value, min, max, renderOptions.PotentialDisplayMode)
                : GetConductivityColor(value, min, max, renderOptions);
            positions[i] = (float)t;
        }

        SKRect rect;
        SKPoint start;
        SKPoint end;
        if (colorBarOptions.Orientation == ColorBarOrientation.Vertical)
        {
            rect = new SKRect(info.Width * 0.25f, 10f, info.Width * 0.6f, Math.Max(20f, info.Height - 20f));
            start = new SKPoint(rect.Left, rect.Bottom);
            end = new SKPoint(rect.Left, rect.Top);
        }
        else
        {
            rect = new SKRect(0f, 0f, info.Width, info.Height);
            start = new SKPoint(rect.Left, rect.Bottom);
            end = new SKPoint(rect.Right, rect.Bottom);
        }

        using var shader = SKShader.CreateLinearGradient(start, end, colors, positions, SKShaderTileMode.Clamp);
        using var fill = new SKPaint { Shader = shader, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var border = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = colorBarOptions.BorderColor,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        using var text = new SKPaint
        {
            Color = colorBarOptions.TextColor,
            TextSize = colorBarOptions.TextSize,
            IsAntialias = true
        };

        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, border);

        if (colorBarOptions.Orientation == ColorBarOrientation.Vertical)
        {
            float labelX = rect.Right + 6f;
            canvas.DrawText(max.ToString("0.###"), labelX, rect.Top + text.TextSize, text);
            if (colorBarOptions.ShowMidpointLabel)
                canvas.DrawText(((min + max) * 0.5).ToString("0.###"), labelX, rect.MidY + text.TextSize * 0.35f, text);
            canvas.DrawText(min.ToString("0.###"), labelX, rect.Bottom, text);
            return;
        }

        float maxWidth = text.MeasureText(max.ToString("F2"));
        canvas.DrawText(min.ToString("F2"), rect.Left, rect.Bottom - 2f, text);
        canvas.DrawText(max.ToString("F2"), rect.Right - maxWidth, rect.Bottom - 2f, text);
    }
}
