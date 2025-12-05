using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using SkiaSharp;
using Utility.Classes;

using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;

namespace Utility.Rendering;

public static class ReconstructionVideoRenderer
{
    private static readonly SKColor DistributionCanvasBackgroundColor = SKColor.Parse("#1A2436");
    private static readonly SKColor ChartGradientTopColor = SKColor.Parse("#23354D");
    private static readonly SKColor ChartGradientBottomColor = SKColor.Parse("#151E2D");
    private static readonly SKColor ChartLineColor = SKColor.Parse("#3A9CED");
    private static readonly SKColor ChartAreaFillColor = new SKColor(58, 156, 237, 90);
    private static readonly SKColor ChartAxisColor = SKColor.Parse("#5B6F94");
    private static readonly SKColor ChartGridColor = new SKColor(255, 255, 255, 50);
    private static readonly SKColor ChartPointColor = SKColor.Parse("#A7D2FF");
    private static readonly SKColor ChartPointOutlineColor = SKColor.Parse("#0B1C2F");
    private static readonly SKColor ChartPrimaryTextColor = new SKColor(198, 212, 245);
    private static readonly SKColor ChartSecondaryTextColor = new SKColor(157, 170, 211);

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
        (0.83, SKColor.Parse("#EFBE71")),
        (1.0, SKColor.Parse("#FCFDBF"))
    };

    private static readonly (double Position, SKColor Color)[] CividisPalette =
    {
        (0.0, SKColor.Parse("#00204C")),
        (0.2, SKColor.Parse("#214F72")),
        (0.4, SKColor.Parse("#4B7380")),
        (0.6, SKColor.Parse("#7A8D82")),
        (0.8, SKColor.Parse("#A5A87A")),
        (1.0, SKColor.Parse("#D6D645"))
    };

    private static readonly (double Position, SKColor Color)[] CoolWarmPalette =
    {
        (0.0, SKColor.Parse("#3B4CC0")),
        (0.25, SKColor.Parse("#6889FF")),
        (0.5, SKColor.Parse("#F7F7F7")),
        (0.75, SKColor.Parse("#FF9E7C")),
        (1.0, SKColor.Parse("#B40426"))
    };

    public enum DistributionSnapshotType
    {
        Original,
        Initial,
        Reconstructed
    }

    private enum DistributionSection
    {
        Potential,
        Adjoint,
        Gradient,
        Original,
        Initial,
        Reconstructed
    }

    private readonly record struct FemTransform(
        float Scale,
        float MarginX,
        float MarginY,
        float MinX,
        float MinY,
        float CanvasHeight)
    {
        public SKPoint ToCanvas(FEMVertex v)
            => new((float)(v.X - MinX) * Scale + MarginX,
                   CanvasHeight - ((float)(v.Y - MinY) * Scale + MarginY));
    }

    public static SKSizeI NormalizeSize(SKSize size, int fallbackWidth, int fallbackHeight)
    {
        int width = (int)Math.Round(size.Width);
        int height = (int)Math.Round(size.Height);
        if (width <= 0) width = fallbackWidth;
        if (height <= 0) height = fallbackHeight;
        return new SKSizeI(width, height);
    }

    public static SKSizeI CalculateFrameDimensions(SKSizeI distributionSize,
                                                   SKSizeI colorbarSize,
                                                   SKSizeI residualSize)
    {
        int cellWidth = Math.Max(distributionSize.Width, 1);
        int cellHeight = Math.Max(distributionSize.Height, 1);
        int colorbarHeight = Math.Max(colorbarSize.Height, 1);
        int residualChartHeight = Math.Max(residualSize.Height, 1);

        const int outerMargin = 32;
        const int columnSpacing = 32;
        const int rowSpacing = 40;
        const int cellPadding = 18;
        const int labelHeight = 32;
        const int colorbarSpacing = 10;
        const int residualPadding = 24;
        const int residualLabelHeight = 36;

        int cellContainerWidth = cellPadding * 2 + cellWidth;
        int cellContainerHeight = cellPadding + labelHeight + cellHeight + colorbarSpacing + colorbarHeight + cellPadding;
        int gridHeight = cellContainerHeight * 2 + rowSpacing;
        int totalWidth = outerMargin * 2 + cellContainerWidth * 3 + columnSpacing * 2;
        int residualSectionHeight = residualLabelHeight + residualPadding * 2 + residualChartHeight;
        int totalHeight = outerMargin * 2 + gridHeight + residualSectionHeight;

        return new SKSizeI(totalWidth, totalHeight);
    }

    public static ReconstructionResult? FindResultForFrame(IReadOnlyList<ReconstructionResult> results,
                                                            int frameIndex,
                                                            out int resultIndex)
    {
        int cumulative = 0;
        for (int i = 0; i < results.Count; i++)
        {
            int frameCount = results[i].Frames.Count;
            if (frameIndex < cumulative + frameCount)
            {
                resultIndex = i;
                return results[i];
            }
            cumulative += frameCount;
        }

        resultIndex = results.Count - 1;
        return results.Count > 0 ? results[^1] : null;
    }

    public static void SaveDistributionSnapshot(
        string directory,
        string fileName,
        IDiscretization? fallbackDiscretization,
        ReconstructionFrame frame,
        ReconstructionResult? context,
        DistributionSnapshotType snapshotType,
        string title,
        IReadOnlyList<(string Label, string Value)> metadata,
        PotentialDisplayMode potentialMode,
        ConductivityDisplayMode conductivityMode)
    {
        // Layout constants
        const int outerMargin = 24;
        const int spacing = 24;
        const int rightPanelWidth = 320;
        const int titleHeight = 40;
        const int colorbarHeight = 52;

        var contentSize = new SKSizeI(800, 600);
        var colorbarSize = new SKSizeI(contentSize.Width, colorbarHeight);

        var section = snapshotType switch
        {
            DistributionSnapshotType.Original => DistributionSection.Original,
            DistributionSnapshotType.Initial => DistributionSection.Initial,
            _ => DistributionSection.Reconstructed
        };

        var discretization = context?.Discretization ?? fallbackDiscretization
            ?? throw new ArgumentNullException(nameof(fallbackDiscretization),
                "A discretization is required to render reconstruction distributions.");

        using var contentImage = RenderDistributionImage(section, discretization, frame, context, contentSize, potentialMode, conductivityMode);
        using var colorbarImage = RenderColorbarImage(section, discretization, frame, context, colorbarSize, potentialMode, conductivityMode);

        int width = outerMargin + contentSize.Width + spacing + rightPanelWidth + outerMargin;
        int height = outerMargin + titleHeight + spacing + contentSize.Height + spacing + colorbarHeight + outerMargin;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(21, 30, 45));

        // Title
        using (var titlePaint = new SKPaint
        {
            Color = new SKColor(240, 244, 255),
            TextSize = 28,
            IsAntialias = true,
            TextAlign = SKTextAlign.Left,
            FakeBoldText = true
        })
        {
            canvas.DrawText(title, outerMargin, outerMargin + titlePaint.TextSize, titlePaint);
        }

        float contentX = outerMargin;
        float contentY = outerMargin + titleHeight + spacing;
        canvas.DrawImage(contentImage, new SKRect(contentX, contentY, contentX + contentSize.Width, contentY + contentSize.Height));

        float colorbarY = contentY + contentSize.Height + spacing;
        canvas.DrawImage(colorbarImage, new SKRect(contentX, colorbarY, contentX + contentSize.Width, colorbarY + colorbarHeight));

        // Right metadata panel
        var panelX = contentX + contentSize.Width + spacing;
        var panelY = contentY;
        var panelRect = new SKRect(panelX, panelY, panelX + rightPanelWidth, colorbarY + colorbarHeight - panelY);
        using var panelBg = new SKPaint { Color = new SKColor(26, 36, 54), IsAntialias = true };
        using var panelBorder = new SKPaint { Color = new SKColor(58, 124, 165), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawRoundRect(panelRect, 12, 12, panelBg);
        canvas.DrawRoundRect(panelRect, 12, 12, panelBorder);

        using var labelPaint = new SKPaint { Color = ChartSecondaryTextColor, TextSize = 20, IsAntialias = true, TextAlign = SKTextAlign.Left };
        using var valuePaint = new SKPaint { Color = ChartPrimaryTextColor, TextSize = 22, IsAntialias = true, TextAlign = SKTextAlign.Left };

        float textX = panelX + 16;
        float textY = panelY + 28;
        float lineGap = 8;
        foreach (var (Label, Value) in metadata)
        {
            canvas.DrawText(Label + ":", textX, textY, labelPaint);
            textY += labelPaint.TextSize + 2;

            var valueLines = Value.Split('\n');
            for (int i = 0; i < valueLines.Length; i++)
            {
                canvas.DrawText(valueLines[i], textX, textY, valuePaint);
                textY += valuePaint.TextSize;

                if (i < valueLines.Length - 1)
                    textY += lineGap / 2f;
            }

            textY += lineGap;
        }

        // Save PNG
        string path = Path.Combine(directory, fileName);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        data.SaveTo(fs);
    }

    [Obsolete]
    public static SKImage RenderFrameSnapshot(ReconstructionFrame frame,
                                              ReconstructionResult? context,
                                              IDiscretization? fallbackDiscretization,
                                              IReadOnlyList<double> residualHistory,
                                              int residualCount,
                                              SKSizeI distributionSize,
                                              SKSizeI colorbarSize,
                                              SKSizeI residualSize,
                                              PotentialDisplayMode potentialMode,
                                              ConductivityDisplayMode conductivityMode)
    {
        var discretization = context?.Discretization ?? fallbackDiscretization
            ?? throw new ArgumentNullException(nameof(fallbackDiscretization),
                "A discretization is required to render reconstruction frames.");

        int cellWidth = Math.Max(distributionSize.Width, 1);
        int cellHeight = Math.Max(distributionSize.Height, 1);
        int colorbarHeight = Math.Max(colorbarSize.Height, 1);
        int residualChartHeight = Math.Max(residualSize.Height, 1);

        const int outerMargin = 32;
        const int columnSpacing = 32;
        const int rowSpacing = 40;
        const int cellPadding = 18;
        const int labelHeight = 32;
        const int colorbarSpacing = 10;
        const int residualPadding = 24;
        const int residualLabelHeight = 36;

        int cellContainerWidth = cellPadding * 2 + cellWidth;
        int cellContainerHeight = cellPadding + labelHeight + cellHeight + colorbarSpacing + colorbarHeight + cellPadding;
        int gridHeight = cellContainerHeight * 2 + rowSpacing;
        int totalWidth = outerMargin * 2 + cellContainerWidth * 3 + columnSpacing * 2;
        int residualSectionHeight = residualLabelHeight + residualPadding * 2 + residualChartHeight;
        int totalHeight = outerMargin * 2 + gridHeight + residualSectionHeight;

        using var surface = SKSurface.Create(new SKImageInfo(totalWidth, totalHeight));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(21, 30, 45));

        var sections = new (DistributionSection Section, string Title)[]
        {
            (DistributionSection.Potential, "Calculated Potential [mV]"),
            (DistributionSection.Adjoint, "Adjoint Potential [mV]"),
            (DistributionSection.Gradient, "Conductivity Gradient [∂Ω]"),
            (DistributionSection.Original, "Original Distribution [Ω]"),
            (DistributionSection.Initial, "Initial Distribution [Ω]"),
            (DistributionSection.Reconstructed, "Reconstructed Distribution [Ω]")
        };

        using var cellBackgroundPaint = new SKPaint { Color = new SKColor(26, 36, 54), IsAntialias = true };
        using var cellBorderPaint = new SKPaint
        {
            Color = new SKColor(58, 124, 165),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };
        using var titlePaint = new SKPaint
        {
            Color = new SKColor(240, 244, 255),
            TextSize = 20,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
            FakeBoldText = true
        };

        for (int i = 0; i < sections.Length; i++)
        {
            int row = i / 3;
            int col = i % 3;
            float x = outerMargin + col * (cellContainerWidth + columnSpacing);
            float y = outerMargin + row * (cellContainerHeight + rowSpacing);

            using var contentImage = RenderDistributionImage(sections[i].Section,
                                                              discretization,
                                                              frame,
                                                              context,
                                                              distributionSize,
                                                              potentialMode,
                                                              conductivityMode);

            using var colorbarImage = RenderColorbarImage(sections[i].Section,
                                                          discretization,
                                                          frame,
                                                          context,
                                                          new SKSizeI(cellWidth, colorbarHeight),
                                                          potentialMode,
                                                          conductivityMode);

            DrawDistributionCell(canvas,
                                 sections[i].Title,
                                 x,
                                 y,
                                 cellContainerWidth,
                                 cellContainerHeight,
                                 cellWidth,
                                 cellHeight,
                                 colorbarHeight,
                                 cellPadding,
                                 labelHeight,
                                 colorbarSpacing,
                                 contentImage,
                                 colorbarImage,
                                 cellBackgroundPaint,
                                 cellBorderPaint,
                                 titlePaint);
        }

        using var residualBackgroundPaint = new SKPaint { Color = new SKColor(34, 48, 70), IsAntialias = true };
        using var residualBorderPaint = new SKPaint
        {
            Color = new SKColor(58, 124, 165),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };

        float residualX = outerMargin;
        float residualY = outerMargin + gridHeight + residualPadding;
        float residualWidth = totalWidth - outerMargin * 2;

        var residualRect = new SKRect(residualX,
                                      residualY,
                                      residualX + residualWidth,
                                      residualY + residualLabelHeight + residualPadding);
        canvas.DrawRoundRect(residualRect, 12, 12, residualBackgroundPaint);
        canvas.DrawRoundRect(residualRect, 12, 12, residualBorderPaint);

        using var residualTitlePaint = new SKPaint
        {
            Color = new SKColor(240, 244, 255),
            TextSize = 24,
            IsAntialias = true,
            TextAlign = SKTextAlign.Left,
            FakeBoldText = true
        };
        canvas.DrawText("Residual History",
                        residualX + cellPadding,
                        residualY + residualTitlePaint.TextSize + 4,
                        residualTitlePaint);

        var residualChartRect = new SKRect(residualX + residualPadding,
                                           residualY + residualLabelHeight,
                                           residualX + residualWidth - residualPadding,
                                           residualY + residualLabelHeight + 0);

        DrawResidualTrend(canvas,
                          residualChartRect,
                          residualHistory,
                          residualCount,
                          ChartGradientTopColor,
                          ChartGradientBottomColor);

        return surface.Snapshot();
    }

    private static SKImage RenderDistributionImage(DistributionSection section,
                                                   IDiscretization discretization,
                                                   ReconstructionFrame frame,
                                                   ReconstructionResult? context,
                                                   SKSizeI size,
                                                   PotentialDisplayMode potentialMode,
                                                   ConductivityDisplayMode conductivityMode)
    {
        var info = new SKImageInfo(size.Width, size.Height);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        switch (section)
        {
            case DistributionSection.Potential when frame.CalculatedPotentialDistribution is { } potential:
                if (discretization is FEMMesh fem)
                    DrawFemPotential(canvas, info, fem, potential, potentialMode);
                else if (discretization is LBMGrid lbm)
                    DrawLbmField(canvas, info, lbm, potential.Potentials, true, potentialMode, conductivityMode);
                else
                    canvas.Clear(DistributionCanvasBackgroundColor);
                break;

            case DistributionSection.Adjoint when frame.CalculatedAdjointDistribution is { } adjoint:
                if (discretization is FEMMesh femAdj)
                    DrawFemPotential(canvas, info, femAdj, adjoint, potentialMode);
                else if (discretization is LBMGrid lbmAdj)
                    DrawLbmField(canvas, info, lbmAdj, adjoint.Potentials, true, potentialMode, conductivityMode);
                else
                    canvas.Clear(DistributionCanvasBackgroundColor);
                break;

            case DistributionSection.Gradient when frame.ConductivityGradient != null:
                if (discretization is FEMMesh femGrad)
                    DrawFemConductivity(canvas, info, femGrad, frame.ConductivityGradient, conductivityMode);
                else if (discretization is LBMGrid lbmGrad)
                    DrawLbmField(canvas, info, lbmGrad, frame.ConductivityGradient.Conductivities, false, potentialMode, conductivityMode);
                else
                    canvas.Clear(DistributionCanvasBackgroundColor);
                break;

            case DistributionSection.Original:
                {
                    var cd = context?.OriginalConductivityDistribution
                             ?? discretization.GetConductivityDistribution();
                    if (discretization is FEMMesh femOrig)
                        DrawFemConductivity(canvas, info, femOrig, cd, conductivityMode);
                    else if (discretization is LBMGrid lbmOrig)
                        DrawLbmField(canvas, info, lbmOrig, cd.Conductivities, false, potentialMode, conductivityMode);
                    else
                        canvas.Clear(DistributionCanvasBackgroundColor);
                    break;
                }

            case DistributionSection.Initial:
                {
                    var cd = context?.InitialConductivitiyDistribution
                             ?? discretization.GetConductivityDistribution();
                    if (discretization is FEMMesh femInit)
                        DrawFemConductivity(canvas, info, femInit, cd, conductivityMode);
                    else if (discretization is LBMGrid lbmInit)
                        DrawLbmField(canvas, info, lbmInit, cd.Conductivities, false, potentialMode, conductivityMode);
                    else
                        canvas.Clear(DistributionCanvasBackgroundColor);
                    break;
                }

            case DistributionSection.Reconstructed:
                {
                    var cd = context?.ReconstructedConductivityDistribution
                             ?? discretization.GetConductivityDistribution();
                    if (discretization is FEMMesh femRec)
                        DrawFemConductivity(canvas, info, femRec, cd, conductivityMode);
                    else if (discretization is LBMGrid lbmRec)
                        DrawLbmField(canvas, info, lbmRec, cd.Conductivities, false, potentialMode, conductivityMode);
                    else
                        canvas.Clear(DistributionCanvasBackgroundColor);
                    break;
                }

            default:
                canvas.Clear(DistributionCanvasBackgroundColor);
                break;
        }

        return surface.Snapshot();
    }

    private static SKImage RenderColorbarImage(DistributionSection section,
                                              IDiscretization discretization,
                                              ReconstructionFrame frame,
                                              ReconstructionResult? context,
                                              SKSizeI size,
                                              PotentialDisplayMode potentialMode,
                                              ConductivityDisplayMode conductivityMode)
    {
        var info = new SKImageInfo(size.Width, size.Height);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        bool isPotential = section is DistributionSection.Potential or DistributionSection.Adjoint;
        double min = 0;
        double max = 0;
        bool hasValues = false;

        switch (section)
        {
            case DistributionSection.Potential when frame.CalculatedPotentialDistribution is { } pot:
                min = pot.Potentials.Values.Min();
                max = pot.Potentials.Values.Max();
                hasValues = true;
                break;

            case DistributionSection.Adjoint when frame.CalculatedAdjointDistribution is { } adj:
                min = adj.Potentials.Values.Min();
                max = adj.Potentials.Values.Max();
                hasValues = true;
                break;

            case DistributionSection.Gradient when frame.ConductivityGradient != null:
                min = frame.ConductivityGradient.Conductivities.Values.Min();
                max = frame.ConductivityGradient.Conductivities.Values.Max();
                hasValues = true;
                break;

            case DistributionSection.Original:
                {
                    var cd = context?.OriginalConductivityDistribution
                             ?? discretization.GetConductivityDistribution();
                    min = cd.Conductivities.Values.Min();
                    max = cd.Conductivities.Values.Max();
                    hasValues = true;
                    break;
                }

            case DistributionSection.Initial:
                {
                    var cd = context?.InitialConductivitiyDistribution
                             ?? discretization.GetConductivityDistribution();
                    min = cd.Conductivities.Values.Min();
                    max = cd.Conductivities.Values.Max();
                    hasValues = true;
                    break;
                }

            case DistributionSection.Reconstructed:
                {
                    var cd = context?.ReconstructedConductivityDistribution
                             ?? discretization.GetConductivityDistribution();
                    min = cd.Conductivities.Values.Min();
                    max = cd.Conductivities.Values.Max();
                    hasValues = true;
                    break;
                }
        }

        if (!hasValues)
        {
            canvas.Clear(DistributionCanvasBackgroundColor);
            return surface.Snapshot();
        }

        DrawColorBar(canvas,
                     info,
                     min,
                     max,
                     isPotential,
                     potentialMode,
                     conductivityMode);

        return surface.Snapshot();
    }

    [Obsolete]
    private static void DrawDistributionCell(SKCanvas canvas,
                                             string title,
                                             float x,
                                             float y,
                                             float containerWidth,
                                             float containerHeight,
                                             float contentWidth,
                                             float contentHeight,
                                             float colorbarHeight,
                                             float padding,
                                             float labelHeight,
                                             float colorbarSpacing,
                                             SKImage contentImage,
                                             SKImage colorbarImage,
                                             SKPaint backgroundPaint,
                                             SKPaint borderPaint,
                                             SKPaint titlePaint)
    {
        var rect = new SKRect(x, y, x + containerWidth, y + containerHeight);
        canvas.DrawRoundRect(rect, 12, 12, backgroundPaint);
        canvas.DrawRoundRect(rect, 12, 12, borderPaint);

        float titleY = y + padding + titlePaint.TextSize;
        canvas.DrawText(title, x + containerWidth / 2f, titleY, titlePaint);

        float imageX = x + padding;
        float imageY = y + padding + labelHeight;
        canvas.DrawImage(contentImage, new SKRect(imageX, imageY, imageX + contentWidth, imageY + contentHeight));

        float colorbarY = imageY + contentHeight + colorbarSpacing;
        canvas.DrawImage(colorbarImage,
                         new SKRect(imageX,
                                    colorbarY,
                                    imageX + contentWidth,
                                    colorbarY + colorbarHeight));
    }

    [Obsolete]
    private static void DrawColorBar(SKCanvas canvas,
                                     SKImageInfo info,
                                     double min,
                                     double max,
                                     bool isPotential,
                                     PotentialDisplayMode potentialMode,
                                     ConductivityDisplayMode conductivityMode)
    {
        using var gradient = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0),
                                                   new SKPoint(info.Width, 0),
                                                   Enumerable.Range(0, info.Width).Select(i =>
                                                   {
                                                       double value = min + (max - min) * i / Math.Max(info.Width - 1, 1);
                                                       return isPotential
                                                           ? GetPotentialColor(value, min, max, potentialMode)
                                                           : GetConductivityColor(value, min, max, conductivityMode);
                                                   }).ToArray(),
                                                   null,
                                                   SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, info.Width, info.Height), gradient);

        using var border = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.White,
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawRect(new SKRect(0, 0, info.Width, info.Height), border);

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 18,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        textPaint.TextAlign = SKTextAlign.Left;
        canvas.DrawText(min.ToString("F2"), 0, info.Height - 4, textPaint);
        textPaint.TextAlign = SKTextAlign.Center;
        canvas.DrawText(((min + max) * 0.5).ToString("F2"), info.Width / 2f, info.Height - 4, textPaint);
        textPaint.TextAlign = SKTextAlign.Right;
        canvas.DrawText(max.ToString("F2"), info.Width, info.Height - 4, textPaint);
    }

    [Obsolete]
    private static void DrawResidualTrend(SKCanvas canvas,
                                          SKRect rect,
                                          IReadOnlyList<double> history,
                                          int residualCount,
                                          SKColor gradientTop,
                                          SKColor gradientBottom)
    {
        using var gradientPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top),
                                                   new SKPoint(rect.Left, rect.Bottom),
                                                   new[] { gradientTop, gradientBottom },
                                                   null,
                                                   SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(rect, gradientPaint);

        using var borderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = ChartAxisColor,
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawRect(rect, borderPaint);

        if (history.Count == 0 || residualCount == 0)
            return;

        int count = Math.Min(residualCount, history.Count);
        var values = history.Take(count).ToArray();
        double maxVal = values.Max();
        double minVal = values.Min();
        if (Math.Abs(maxVal - minVal) < 1e-6)
        {
            maxVal += 1e-6;
            minVal -= 1e-6;
        }

        using var gridPaint = new SKPaint
        {
            Color = ChartGridColor,
            StrokeWidth = 1,
            IsAntialias = true
        };

        int gridLines = 5;
        for (int i = 1; i < gridLines; i++)
        {
            float y = rect.Top + rect.Height * i / gridLines;
            canvas.DrawLine(rect.Left, y, rect.Right, y, gridPaint);
        }

        using var pathPaint = new SKPaint
        {
            Color = ChartLineColor,
            StrokeWidth = 3,
            IsAntialias = true
        };

        using var fillPaint = new SKPaint
        {
            Color = ChartAreaFillColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        var path = new SKPath();
        float stepX = rect.Width / Math.Max(count - 1, 1);
        for (int i = 0; i < count; i++)
        {
            float x = rect.Left + stepX * i;
            float norm = (float)((values[i] - minVal) / (maxVal - minVal));
            float y = rect.Bottom - norm * rect.Height;
            if (i == 0)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }

        var fillPath = new SKPath(path);
        fillPath.LineTo(rect.Right, rect.Bottom);
        fillPath.LineTo(rect.Left, rect.Bottom);
        fillPath.Close();
        canvas.DrawPath(fillPath, fillPaint);
        canvas.DrawPath(path, pathPaint);

        using var pointPaint = new SKPaint
        {
            Color = ChartPointColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var pointBorderPaint = new SKPaint
        {
            Color = ChartPointOutlineColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };

        for (int i = 0; i < count; i++)
        {
            float x = rect.Left + stepX * i;
            float norm = (float)((values[i] - minVal) / (maxVal - minVal));
            float y = rect.Bottom - norm * rect.Height;
            canvas.DrawCircle(x, y, 4, pointPaint);
            canvas.DrawCircle(x, y, 4, pointBorderPaint);
        }

        using var labelPaint = new SKPaint
        {
            Color = ChartPrimaryTextColor,
            TextSize = 18,
            IsAntialias = true,
            TextAlign = SKTextAlign.Left
        };
        canvas.DrawText($"Min: {minVal:F4}", rect.Left + 12, rect.Top + 24, labelPaint);
        canvas.DrawText($"Max: {maxVal:F4}", rect.Left + 12, rect.Top + 48, labelPaint);

        using var countPaint = new SKPaint
        {
            Color = ChartSecondaryTextColor,
            TextSize = 18,
            IsAntialias = true,
            TextAlign = SKTextAlign.Right
        };
        canvas.DrawText($"Samples: {count}", rect.Right - 12, rect.Top + 24, countPaint);
    }

    private static FemTransform ComputeFemTransform(FEMMesh mesh, SKImageInfo info)
    {
        const float pad = 10f;
        float availW = info.Width - 2 * pad;
        float availH = info.Height - 2 * pad;

        float minX = (float)mesh.Vertices.Min(v => v.X);
        float minY = (float)mesh.Vertices.Min(v => v.Y);
        float maxX = (float)mesh.Vertices.Max(v => v.X);
        float maxY = (float)mesh.Vertices.Max(v => v.Y);
        float meshWidth = maxX - minX;
        float meshHeight = maxY - minY;
        float scale = Math.Min(availW / meshWidth, availH / meshHeight);
        float usedW = meshWidth * scale;
        float usedH = meshHeight * scale;
        float marginX = pad + (availW - usedW) / 2f;
        float marginY = pad + (availH - usedH) / 2f;
        return new FemTransform(scale, marginX, marginY, minX, minY, info.Height);
    }

    private static void DrawFemConductivity(SKCanvas canvas,
                                            SKImageInfo info,
                                            FEMMesh mesh,
                                            ConductivityDistribution cd,
                                            ConductivityDisplayMode mode)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);
        var transform = ComputeFemTransform(mesh, info);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };
        double minVal = cd.Conductivities.Values.Min();
        double maxVal = cd.Conductivities.Values.Max();
        if (Math.Abs(maxVal - minVal) < 1e-12) maxVal = minVal + 1e-12;
        foreach (var elem in mesh.GetElements().Cast<FEMElement>())
        {
            double val = cd.GetConductivity(elem.Id);
            fill.Color = GetConductivityColor(val, minVal, maxVal, mode);
            using var path = new SKPath();
            path.MoveTo(transform.ToCanvas(elem.Vertices[0]));
            path.LineTo(transform.ToCanvas(elem.Vertices[1]));
            path.LineTo(transform.ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
        }
    }

    private static void DrawFemPotential(SKCanvas canvas,
                                         SKImageInfo info,
                                         FEMMesh mesh,
                                         PotentialDistribution pd,
                                         PotentialDisplayMode mode)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);
        var transform = ComputeFemTransform(mesh, info);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true };
        double minVal = pd.Potentials.Values.Min();
        double maxVal = pd.Potentials.Values.Max();
        if (Math.Abs(maxVal - minVal) < 1e-12) maxVal = minVal + 1e-12;
        foreach (var elem in mesh.GetElements().Cast<FEMElement>())
        {
            double avg = elem.Vertices.Average(v => pd.GetPotential(v.GlobalId));
            fill.Color = GetPotentialColor(avg, minVal, maxVal, mode);
            using var path = new SKPath();
            path.MoveTo(transform.ToCanvas(elem.Vertices[0]));
            path.LineTo(transform.ToCanvas(elem.Vertices[1]));
            path.LineTo(transform.ToCanvas(elem.Vertices[2]));
            path.Close();
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
        }
    }

    private static void DrawLbmField(SKCanvas canvas,
                                     SKImageInfo info,
                                     LBMGrid mesh,
                                     IReadOnlyDictionary<int, double> values,
                                     bool isPotential,
                                     PotentialDisplayMode potentialMode,
                                     ConductivityDisplayMode conductivityMode)
    {
        canvas.Clear(DistributionCanvasBackgroundColor);
        float cw = info.Width / mesh.Nx;
        float ch = info.Height / mesh.Ny;
        double minVal = values.Values.Min();
        double maxVal = values.Values.Max();
        if (Math.Abs(maxVal - minVal) < 1e-12) maxVal = minVal + 1e-12;
        using var paint = new SKPaint { Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 };

        for (int y = 0; y < mesh.Ny; y++)
        {
            for (int x = 0; x < mesh.Nx; x++)
            {
                var el = mesh.GetElementAt(x, y);
                double val = values[el.Id];
                paint.Color = el.IsWall
                    ? SKColors.Black
                    : isPotential
                        ? GetPotentialColor(val, minVal, maxVal, potentialMode)
                        : GetConductivityColor(val, minVal, maxVal, conductivityMode);
                var r = SKRect.Create(x * cw, y * ch, cw, ch);
                canvas.DrawRect(r, paint);
                canvas.DrawRect(r, stroke);
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

    private static SKColor GetConductivityColor(double val, double min, double max, ConductivityDisplayMode mode)
    {
        double norm = (val - min) / (max - min);
        norm = double.IsNaN(norm) ? 0.0 : Math.Clamp(norm, 0.0, 1.0);

        return mode switch
        {
            ConductivityDisplayMode.Classic => ColorForValue(val, min, max),
            ConductivityDisplayMode.EnhancedDiverging => InterpolatePalette(EnhancedDivergingPalette, norm),
            ConductivityDisplayMode.Rainbow => SKColor.FromHsv((float)(240.0 * (1.0 - norm)), 90f, 100f),
            ConductivityDisplayMode.MatlabJet => InterpolatePalette(MatlabJetPalette, norm),
            ConductivityDisplayMode.Parula => InterpolatePalette(ParulaPalette, norm),
            ConductivityDisplayMode.Viridis => InterpolatePalette(ViridisPalette, norm),
            ConductivityDisplayMode.Plasma => InterpolatePalette(PlasmaPalette, norm),
            ConductivityDisplayMode.Magma => InterpolatePalette(MagmaPalette, norm),
            ConductivityDisplayMode.Cividis => InterpolatePalette(CividisPalette, norm),
            ConductivityDisplayMode.CoolWarm => InterpolatePalette(CoolWarmPalette, norm),
            _ => ColorForValue(val, min, max)
        };
    }

    private static SKColor GetPotentialColor(double val, double min, double max, PotentialDisplayMode mode)
    {
        double mid = (min + max) * 0.5;
        double norm = (val - min) / (max - min);
        norm = Math.Clamp(norm, 0.0, 1.0);
        return mode switch
        {
            PotentialDisplayMode.Grayscale => new SKColor((byte)(norm * 255), (byte)(norm * 255), (byte)(norm * 255)),
            PotentialDisplayMode.Inverted =>
                new SKColor((byte)(255 - ColorForValue(val, min, max).Red),
                            (byte)(255 - ColorForValue(val, min, max).Green),
                            (byte)(255 - ColorForValue(val, min, max).Blue)),
            PotentialDisplayMode.Heatmap => new SKColor(255, (byte)(255 * (1 - norm)), 0),
            PotentialDisplayMode.Rainbow => SKColor.FromHsv((float)(norm * 360f), 100f, 100f),
            _ => ColorForValue(val, min, max),
        };
    }
}
