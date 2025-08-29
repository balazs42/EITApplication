using Workspace = Utility.Classes.Application.Workspace;

namespace Utility.Logger
{
    public sealed class WorkspaceLogger : ILogger
    {
        public void LogError(string error)
        {
            Workspace.AddErrorMessage(error);
        }

        public void LogInfo(string info)
        {
            Workspace.AddInfoMessage(info);
        }

        public void LogWarning(string warning)
        {
            Workspace.AddWarningMessage(warning);
        }
    }
}
