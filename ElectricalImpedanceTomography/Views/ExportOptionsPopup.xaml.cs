using System;
using CommunityToolkit.Maui.Views;

namespace ElectricalImpedanceTomography.Views;

public partial class ExportOptionsPopup : Popup
{
    public ExportOptionsPopup()
    {
        InitializeComponent();
    }

    private void OnExportVideoClicked(object? sender, EventArgs e)
        => Close(ExportMode.Video);

    private void OnExportCsvClicked(object? sender, EventArgs e)
        => Close(ExportMode.Csv);

    private void OnCancelClicked(object? sender, EventArgs e)
        => Close(null);
}

public enum ExportMode
{
    Video,
    Csv
}
