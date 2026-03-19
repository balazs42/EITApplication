using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.Controls;
using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using System;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Factories;
using Utility.Rendering;

namespace ElectricalImpedanceTomography.Views;

public partial class InitialDistributionEditorPopup : Popup
{
    private readonly InitialDistributionEditorViewModel _viewModel;
    private readonly DiscretizationCanvasRenderer _renderer = new();

    public event EventHandler? DistributionChanged;

    public InitialDistributionEditorPopup(IDiscretization discretization,
                                          ConductivityDistribution initialDistribution,
                                          ConductivityDistribution? originalDistribution,
                                          InitialDistributionTypes initialType)
    {
        InitializeComponent();

        _viewModel = new InitialDistributionEditorViewModel();
        BindingContext = _viewModel;

        _viewModel.Initialize(discretization, initialDistribution, originalDistribution, initialType);
        _viewModel.DistributionUpdated += OnDistributionUpdated;
    }

    private void OnDistributionUpdated(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PreviewCanvas.InvalidateSurface();
            PreviewColorbarCanvas.InvalidateSurface();
            DistributionChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnPreviewCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _renderer.Draw(
            e.Surface.Canvas,
            e.Info,
            new DiscretizationRenderRequest(_viewModel.Discretization,
                                            DiscretizationRenderMode.Conductivity,
                                            _viewModel.CurrentDistribution),
            new DiscretizationCanvasRenderOptions
            {
                BackgroundColor = SKColor.Parse("#1A2436"),
                ConductivityDisplayMode = ConductivityDisplayMode.Classic
            });
    }

    private void OnColorbarCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _renderer.DrawColorBar(
            e.Surface.Canvas,
            e.Info,
            new DiscretizationRenderRequest(_viewModel.Discretization,
                                            DiscretizationRenderMode.Conductivity,
                                            _viewModel.CurrentDistribution),
            new DiscretizationCanvasRenderOptions
            {
                BackgroundColor = SKColor.Parse("#1A2436"),
                ConductivityDisplayMode = ConductivityDisplayMode.Classic
            });
    }

    private void OnCloseClicked(object sender, EventArgs e)
    {
        Close();
    }

    protected override Task OnClosed(object? result, bool wasDismissedByTappingOutsideOfPopup, CancellationToken token = default)
    {
        base.OnClosed(result, wasDismissedByTappingOutsideOfPopup, token);
        _viewModel.DistributionUpdated -= OnDistributionUpdated;
        return Task.CompletedTask;
    }
}
