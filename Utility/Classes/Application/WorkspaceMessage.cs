namespace Utility.Classes.Application
{
    public enum WorkspaceMessageType
    {
        Log,
        Warning,
        Error,
        Loading,
        Info
    }

    public record WorkspaceMessage(DateTime Time, string Message, WorkspaceMessageType Type);
}
