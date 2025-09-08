using System.Collections.Generic;
using Utility.Classes;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Application
{
    public static class Workspace
    {
        private static User _user { get; set; } = new DefaultUser(0, "No User");
        private static EITReconstructionParameters _reconstructionParameters = new();
        private static IDiscretization? _discretization{ get; set; } = null;
        private static IDiscretization? _originalDiscretization { get; set; } = null;
        private static IDiscretization? _initialDiscretization { get; set; } = null;

        private static int _maxIterationCount = 50;
        private static double _stepSize = 0.001;
        private static double _regularizationWeight = 1e-3;

        private static List<ReconstructionResult> _reconstructionResults = [];
        private static List<ReconstructionFrame> _reconstructionFrames = [];
        private static List<WorkspaceMessage> _messages = [];
        private static ConductivityDistribution? _originalConductivityDistribution = null;
        public static event Action<WorkspaceMessage>? MessageAdded;

        private static bool _initialized = false; 

        public static void Initialize(User user, EITReconstructionParameters? eITReconstructionParameters, IDiscretization? discretization)
        {
            if(_initialized) return;

            _user = user;

            if(eITReconstructionParameters != null)
                _reconstructionParameters = eITReconstructionParameters;
            
            if(discretization != null)
                _discretization = discretization;

            _initialized = true;

            AddMessage("Workspace initialized!");
        }

        public static void SetUser(User user) => _user = user;
        public static void SetReconstructionParameters(EITReconstructionParameters eITReconstructionParameters) => _reconstructionParameters = eITReconstructionParameters;
        public static void SetDiscretization(IDiscretization? discretization) => _discretization = discretization;
        public static void SetOriginalDiscretization(IDiscretization? originalDiscretization) => _originalDiscretization = originalDiscretization;
        public static void SetInitialDiscretization(IDiscretization? initialDiscretization) => _initialDiscretization = initialDiscretization;
        public static void SetReconstructionResults(List<ReconstructionResult> results) => _reconstructionResults = results;
        public static void SetReconstructionFrames(List<ReconstructionFrame> frames) => _reconstructionFrames = frames;
        public static void SetOriginalConductivityDistribution(ConductivityDistribution? sigma) => _originalConductivityDistribution = sigma;

        public static User GetUser() => _user;
        public static EITReconstructionParameters GetReconstructionParameters() => _reconstructionParameters;
        public static IDiscretization? GetDiscretization() => _discretization;
        public static IDiscretization? GetOriginalDiscretization() => _originalDiscretization;
        public static IDiscretization? GetInitialDiscretization() => _initialDiscretization;
        public static List<ReconstructionResult> GetReconstructionResults() => _reconstructionResults;
        public static List<ReconstructionFrame> GetReconstructionFrames() => _reconstructionFrames;
        public static ConductivityDistribution? GetOriginalConductivityDistribution() => _originalConductivityDistribution;

        public static int MaxIterationCount
        {
            get => _maxIterationCount;
            set => _maxIterationCount = value;
        }

        public static double StepSize
        {
            get => _stepSize;
            set => _stepSize = value;
        }

        public static double RegularizationWeight
        {
            get => _regularizationWeight;
            set => _regularizationWeight = value;
        }

        public static void AddReconstructionResultToWorkspace(ReconstructionResult reconstructionResult) => _reconstructionResults.Add(reconstructionResult);
        public static void RemoveReconstructionResultFromWorkspace(int index) => _reconstructionResults.RemoveAt(index);
        public static void AddReconstructionFrameToWorkspace(ReconstructionFrame frame) => _reconstructionFrames.Add(frame);
        public static void ClearReconstructionFrames() => _reconstructionFrames.Clear();

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
