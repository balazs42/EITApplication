namespace Utility.Exports;

public sealed record DataExportResult(bool Success,
                                      string? DirectoryPath,
                                      string? ErrorTitle,
                                      string? ErrorMessage)
{
    public static DataExportResult CreateSuccess(string directoryPath)
        => new(true, directoryPath, null, null);

    public static DataExportResult CreateFailure(string title, string message)
        => new(false, null, title, message);
}
