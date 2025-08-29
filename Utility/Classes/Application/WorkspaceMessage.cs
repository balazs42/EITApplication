using CommunityToolkit.Mvvm.ComponentModel;

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

    [ObservableObject]
    public partial class WorkspaceMessage
    {
        [ObservableProperty]
        private DateTime time;

        [ObservableProperty]
        private string message = string.Empty;

        [ObservableProperty]
        private WorkspaceMessageType type;

        public WorkspaceMessage(DateTime time, string message, WorkspaceMessageType type)
        {
            Time = time;
            Message = message;
            Type = type;
        }
    }
}
