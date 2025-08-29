using Workspace = Utility.Classes.Application.Workspace;

namespace Utility.Logger
{
    public sealed class WorkspaceLogger : ILogger
    {
        public void LogError(string error)
        {
            Workspace.AddLogMessage("ErrorLog", error, Classes.Application.WorkspaceMessageType.Error);
        }

        public void LogInfo(string info)
        {
            Workspace.AddLogMessage("InfoLog", info);
        }

        public void LogWarning(string warning)
        {
            Workspace.AddLogMessage("WarningLog", warning, Classes.Application.WorkspaceMessageType.Warning);
        }
    }
}
