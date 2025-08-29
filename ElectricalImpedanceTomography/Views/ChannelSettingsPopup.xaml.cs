using System;
using CommunityToolkit.Maui.Views;

namespace ElectricalImpedanceTomography.Views;

public partial class ChannelSettingsPopup : Popup
{
    public ChannelSettingsPopup(double gain, double offset)
    {
        InitializeComponent();
        GainEntry.Text = gain.ToString();
        OffsetEntry.Text = offset.ToString();
    }

    void OnCancelClicked(object sender, EventArgs e) => Close();

    void OnSaveClicked(object sender, EventArgs e)
    {
        double.TryParse(GainEntry.Text, out var gain);
        double.TryParse(OffsetEntry.Text, out var offset);
        Close((gain, offset));
    }
}
