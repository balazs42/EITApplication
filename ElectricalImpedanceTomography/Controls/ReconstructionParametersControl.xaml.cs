namespace ElectricalImpedanceTomography.Controls;

public partial class ReconstructionParametersControl : ContentView
{
    public event EventHandler<int>? PotentialModeChanged;

    public ReconstructionParametersControl()
    {
        InitializeComponent();
        PotentialModePicker.SelectedIndexChanged += (s, e) =>
        {
            PotentialModeChanged?.Invoke(this, PotentialModePicker.SelectedIndex);
        };
    }
}
