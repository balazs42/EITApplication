using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Application
{
    public static class Workspace
    {
        private static User _user { get; set; } = new();
        private static EITReconstructionParameters _reconstructionParameters = new();
        private static IMesh? _mesh { get; set; } = null;

        private static List<ReconstructionResult> _reconstructionResults = [];
        private static Dictionary<DateTime, string> _messages = [];

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

            _messages.Add(DateTime.Now, "Workspace initialized!");
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

        public static void AddMessage(string message) => _messages.Add(DateTime.Now, message);
        public static void AddLogMessage(string source, string message) => _messages.Add(DateTime.Now, source + ": " + message);
    }
}
