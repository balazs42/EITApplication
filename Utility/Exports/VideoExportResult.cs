namespace ElectricalImpedanceTomography.ViewModels;

public sealed record VideoExportResult(bool Success,
                                       string? FilePath,
                                       string? ErrorTitle,
                                       string? ErrorMessage)
{
    public static VideoExportResult CreateSuccess(string path)
        => new(true, path, null, null);

    public static VideoExportResult CreateFailure(string title, string message)
        => new(false, null, title, message);
}
