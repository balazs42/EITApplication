using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.Helpers;
using ElectricalImpedanceTomography.ViewModels;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp.Views.Maui;
using System;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Factories;

namespace ElectricalImpedanceTomography.Views;

public partial class InitialDistributionEditorPopup : Popup
{
    private readonly InitialDistributionEditorViewModel _viewModel;

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
        DistributionRenderingHelper.DrawConductivity(e.Surface.Canvas,
                                                    e.Info,
                                                    _viewModel.Discretization,
                                                    _viewModel.CurrentDistribution);
    }

    private void OnColorbarCanvasPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        DistributionRenderingHelper.DrawColorBar(e.Surface.Canvas,
                                                 e.Info,
                                                 _viewModel.Discretization,
                                                 _viewModel.CurrentDistribution);
    }

    private void OnCloseClicked(object sender, EventArgs e)
    {
        Close();
    }

    protected override void OnClosed(PopupClosedEventArgs e)
    {
        base.OnClosed(e);
        _viewModel.DistributionUpdated -= OnDistributionUpdated;
    }
}
