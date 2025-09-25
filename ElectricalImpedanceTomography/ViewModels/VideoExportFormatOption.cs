namespace ElectricalImpedanceTomography.ViewModels;

public enum VideoExportContainer
{
    Mp4,
    Avi
}

public sealed record VideoExportFormatOption(string Title,
                                             string Description,
                                             VideoExportContainer Container,
                                             string FileExtension)
{
    public string DisplayName => $"{Title} ({FileExtension.ToUpperInvariant()})";

    public override string ToString() => DisplayName;
}
