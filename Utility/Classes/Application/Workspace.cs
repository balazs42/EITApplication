using Google.OrTools.Sat;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Application
{
    public static class Workspace
    {
        private static User _user { get; set; } = new DefaultUser(0, "No User");
        private static EITReconstructionParameters _reconstructionParameters = new();
        private static IMesh? _mesh { get; set; } = null;

        private static List<ReconstructionResult> _reconstructionResults = [];
        private static List<WorkspaceMessage> _messages = [];
        public static event Action<WorkspaceMessage>? MessageAdded;

        private static bool _initialized = false; 

        public static void Initialize(User user, EITReconstructionParameters? eITReconstructionParameters, IMesh? mesh)
        {
            if(_initialized) return;

            _user = user;

            if(eITReconstructionParameters != null)
                _reconstructionParameters = eITReconstructionParameters;
            
            if(mesh != null)
                _mesh = mesh;

            _initialized = true;

            AddMessage("Workspace initialized!");
        }

        public static void SetUser(User user) => _user = user;
        public static void SetReconstructionParameters(EITReconstructionParameters eITReconstructionParameters) => _reconstructionParameters = eITReconstructionParameters;
        public static void SetMesh(IMesh mesh) => _mesh = mesh;
        public static void SetReconstructionResults(List<ReconstructionResult> results) => _reconstructionResults = results;

        public static User GetUser() => _user;
        public static EITReconstructionParameters GetReconstructionParameters() => _reconstructionParameters;
        public static IMesh? GetMesh() => _mesh;
        public static List<ReconstructionResult> GetReconstructionResults() => _reconstructionResults;

        public static void AddReconstructionResultToWorkspace(ReconstructionResult reconstructionResult) => _reconstructionResults.Add(reconstructionResult);
        public static void RemoveReconstructionResultFromWorkspace(int index) => _reconstructionResults.RemoveAt(index);

        private static void AddMessage(string message, WorkspaceMessageType type = WorkspaceMessageType.Log)
        {
            DateTime time = DateTime.Now;
            var msg = new WorkspaceMessage(time, message, type);
            _messages.Add(msg);
            MessageAdded?.Invoke(msg);
        }

        public static void AddWarningMessage(string message, WorkspaceMessageType type = WorkspaceMessageType.Warning) => AddMessage("Warning: " + message, type);
        public static void AddErrorMessage(string message, WorkspaceMessageType type = WorkspaceMessageType.Error) => AddMessage("Error: " + message, type);
        public static void AddLoadingMessage(string message, WorkspaceMessageType type = WorkspaceMessageType.Loading) => AddMessage("Loading: " + message, type);
        public static void AddInfoMessage(string message, WorkspaceMessageType type = WorkspaceMessageType.Info) => AddMessage("Info: " + message, type);
        public static void AddLogMessage(string source, string message, WorkspaceMessageType type = WorkspaceMessageType.Log) => AddMessage(source + ": " + message, type);

        public static IReadOnlyList<WorkspaceMessage> GetMessages() => _messages;
    }
}
