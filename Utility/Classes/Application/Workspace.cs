using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction;
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
        private static double _conductivityMinimumBound = 0.1;
        private static double _conductivityMaximumBound = 10.0;

        public const int ReconstructionVideoFramesPerSecond = 10;

        private static List<ReconstructionResult> _reconstructionResults = [];
        private static List<ReconstructionFrame> _reconstructionFrames = [];
        private static List<WorkspaceMessage> _messages = [];
        private static ConductivityDistribution? _originalConductivityDistribution = null;
        public static event Action<WorkspaceMessage>? MessageAdded;

        private static List<FEMElement>? _currentGlobalFemElements;
        private static List<FEMElectrode>? _currentGlobalFemElectrodes;
        private static FEMBoundaryCondition? _currentGlobalFemBoundaryCondition;

        private static List<LBMElement>? _currentGlobalLbmElements;
        private static List<LBMElectrode>? _currentGlobalLbmElectrodes;
        private static LBMBoundaryCondition? _currentGlobalLbmBoundaryCondition;

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
        public static void SetReconstructionParameters(EITReconstructionParameters eITReconstructionParameters)
        {
            _reconstructionParameters = eITReconstructionParameters;
            _conductivityMinimumBound = eITReconstructionParameters.ConductivityMinimumBound;
            _conductivityMaximumBound = eITReconstructionParameters.ConductivityMaximumBound;
            ConductivityClipper.UpdateBounds(_conductivityMinimumBound, _conductivityMaximumBound);
        }
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

        public static void UpdateCurrentGlobalFemElements(FEMMesh mesh)
            => _currentGlobalFemElements = [.. mesh.GetElements().Cast<FEMElement>()];

        public static void SetCurrentGlobalFemElements(List<FEMElement> elements)
            => _currentGlobalFemElements = elements;

        public static List<FEMElement> GetCurrentGlobalFemElements()
            => _currentGlobalFemElements ?? throw new InvalidOperationException("Current FEM elements have not been cached in the workspace.");

        public static void UpdateCurrentGlobalFemElectrodes(FEMMesh mesh)
        {
            mesh.UpdateElectrodeLengths();
            _currentGlobalFemElectrodes = [.. mesh.GetElectrodes().Cast<FEMElectrode>()];
        }

        public static void SetCurrentGlobalFemElectrodes(List<FEMElectrode> electrodes)
            => _currentGlobalFemElectrodes = electrodes;

        public static List<FEMElectrode> GetCurrentGlobalFemElectrodes()
            => _currentGlobalFemElectrodes ?? throw new InvalidOperationException("Current FEM electrodes have not been cached in the workspace.");

        public static void SetCurrentGlobalFemBoundaryCondition(FEMBoundaryCondition boundaryCondition)
            => _currentGlobalFemBoundaryCondition = boundaryCondition;

        public static FEMBoundaryCondition GetCurrentGlobalFemBoundaryCondition()
            => _currentGlobalFemBoundaryCondition ?? throw new InvalidOperationException("Current FEM boundary condition has not been cached in the workspace.");

        public static void UpdateCurrentGlobalLbmElements(LBMGrid mesh)
            => _currentGlobalLbmElements = [.. mesh.GetElements().Cast<LBMElement>()];

        public static void SetCurrentGlobalLbmElements(List<LBMElement> elements)
            => _currentGlobalLbmElements = elements;

        public static List<LBMElement> GetCurrentGlobalLbmElements()
            => _currentGlobalLbmElements ?? throw new InvalidOperationException("Current LBM elements have not been cached in the workspace.");

        public static void UpdateCurrentGlobalLbmElectrodes(LBMGrid mesh)
            => _currentGlobalLbmElectrodes = [.. mesh.GetElectrodes().Cast<LBMElectrode>()];

        public static void SetCurrentGlobalLbmElectrodes(List<LBMElectrode> electrodes)
            => _currentGlobalLbmElectrodes = electrodes;

        public static List<LBMElectrode> GetCurrentGlobalLbmElectrodes()
            => _currentGlobalLbmElectrodes ?? throw new InvalidOperationException("Current LBM electrodes have not been cached in the workspace.");

        public static void SetCurrentGlobalLbmBoundaryCondition(LBMBoundaryCondition boundaryCondition)
            => _currentGlobalLbmBoundaryCondition = boundaryCondition;

        public static LBMBoundaryCondition GetCurrentGlobalLbmBoundaryCondition()
            => _currentGlobalLbmBoundaryCondition ?? throw new InvalidOperationException("Current LBM boundary condition has not been cached in the workspace.");

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

        public static double ConductivityMinimumBound
        {
            get => _conductivityMinimumBound;
            set => _conductivityMinimumBound = value;
        }

        public static double ConductivityMaximumBound
        {
            get => _conductivityMaximumBound;
            set => _conductivityMaximumBound = value;
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
