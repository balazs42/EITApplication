using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceLayer;
using System.Collections.ObjectModel;
using ElectricalImpedanceTomography.Controls;
using Utility.Classes.Application;
using Utility.Classes.Factories;
using Utility.Classes.Discretizer;
using Utility.Classes.ReconstructionParameters;

using Workspace = Utility.Classes.Application.Workspace;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class MainPageViewModel : BaseViewModel
    {
        private readonly IDAQService _daqService;
        private readonly IReconstructionService _reconstructionService;

        [ObservableProperty]
        private ObservableCollection<WorkspaceMessage> debugLog = [];

        [ObservableProperty]
        private double currentAmplitude = 10.0;

        [ObservableProperty]
        private double excitationFrequency = 10.0;

        [ObservableProperty]
        private CurrentImpedanceModel currentImpedanceModel = new();

        [ObservableProperty]
        private bool hardwareConnected;

        [ObservableProperty]
        private EITReconstructionParameters reconstructionParameters = Workspace.GetReconstructionParameters();

        [ObservableProperty]
        private User user = Workspace.GetUser();

        [ObservableProperty]
        private string consoleInput = string.Empty;

        [ObservableProperty]
        private string version = ApplicationInformation.VersionNumber;

        [ObservableProperty]
        private string developedBy = ApplicationInformation.DevelopedBy;

        [ObservableProperty]
        private string latestReleaseDate = ApplicationInformation.LatestReleaseDate;

        partial void OnReconstructionParametersChanged(EITReconstructionParameters value) => Workspace.SetReconstructionParameters(value);
        partial void OnCurrentAmplitudeChanged(double value) => CurrentImpedanceModel.Intensity = (float)Math.Clamp(value, 0, 1);
        partial void OnExcitationFrequencyChanged(double value) => CurrentImpedanceModel.Color = value > 1 ? Colors.Red : Colors.CornflowerBlue;

        
        private static readonly DifferentialEquationSolver[] SolverValues = Enum.GetValues<DifferentialEquationSolver>();
        public IEnumerable<DifferentialEquationSolver> Solvers => SolverValues;
        private static readonly RegularizationTechnique[] RegularizationValues = Enum.GetValues<RegularizationTechnique>();
        public IEnumerable<RegularizationTechnique> Regularizations => RegularizationValues;
        private static readonly ErrorMetric[] ErrorMetricValues = Enum.GetValues<ErrorMetric>();
        public IEnumerable<ErrorMetric> ErrorMetrics => ErrorMetricValues;
        private static readonly NumericOptimizer[] NumericOptimizerValues = Enum.GetValues<NumericOptimizer>();
        public IEnumerable<NumericOptimizer> NumericOptimizers => NumericOptimizerValues;
        private static readonly NumericSolver[] NumericSolverValues = Enum.GetValues<NumericSolver>();
        public IEnumerable<NumericSolver> NumericSolvers => NumericSolverValues;

        public IAsyncRelayCommand<string> NavigateCommand { get; }

        public string HardwareConnectionText => HardwareConnected ? "Connected" : "Disconnected";
        public Color HardwareStatusColor => HardwareConnected ? Colors.Green : Colors.Red;

        partial void OnHardwareConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(HardwareConnectionText));
            OnPropertyChanged(nameof(HardwareStatusColor));
        }

        public event Action? MeshUpdated;

        public MainPageViewModel(IDAQService daqService, IReconstructionService reconstructionService)
        {
            _daqService = daqService;
            _reconstructionService = reconstructionService;

            foreach (var entry in Workspace.GetMessages())
                DebugLog.Add(entry);

            Workspace.MessageAdded += OnWorkspaceMessageAdded;

            NavigateCommand = new AsyncRelayCommand<string>(async (route) => await Shell.Current.GoToAsync(route));

            UpdateHardwareInfo();

            CurrentAmplitude = 1.0;
            ExcitationFrequency = 1.0;

            _commandDescriptions = new()
            {
                {"list", "Lists the available commands"},
                {"generatemesh", $"Generate default mesh. Usage: /Command GenerateMesh [type] ({FormatEnumOptions<DiscretizationType>()})"},
                {"connecthardware", "Starts the connection procedure to the hardware"},
                {"disconnecthardware", "Stops the connection procedure to the hardware"},
                {"initializerreconstruction", "Initializes the reconstruction with the available parameters"},
                {"openpage", "Opens a page. Usage: /Command OpenPage [PageName]"},
                {"changehardwareport", "Changes hardware communication port. Usage: /Command ChangeHardwarePort [COMx]"},
                {"setfrequency", "Sets excitation frequency. Usage: /Command SetFrequency [Freq]"},
                {"loadmesh", "Loads a mesh from file. Usage: /Command LoadMesh [fileName]"},
                {"setsolver", $"Sets differential equation solver. Usage: /Command SetSolver [solver] ({FormatEnumOptions<DifferentialEquationSolver>()})"},
                {"setregularization", $"Sets regularization technique. Usage: /Command SetRegularization [technique] ({FormatEnumOptions<RegularizationTechnique>()})"},
                {"seterrormetric", $"Sets error metric. Usage: /Command SetErrorMetric [metric] ({FormatEnumOptions<ErrorMetric>()})"},
                {"setnumericoptimizer", $"Sets numeric optimizer. Usage: /Command SetNumericOptimizer [optimizer] ({FormatEnumOptions<NumericOptimizer>()})"},
                {"setnumericsolver", $"Sets numeric solver. Usage: /Command SetNumericSolver [solver] ({FormatEnumOptions<NumericSolver>()})"},
                {"setstepsize", "Sets gradient step size"},
                {"setregularizationweight", "Sets regularization weight"},
                {"setmaxiterations", "Sets maximum iteration count"},
                {"clear", "Clears the console"}
            };
        }

        private void OnWorkspaceMessageAdded(WorkspaceMessage message)
        {
            MainThread.BeginInvokeOnMainThread(() => DebugLog.Add(message));
        }

        private void UpdateHardwareInfo()
        {
            try
            {
                var meas = _daqService.GetEITMeasurement();
                HardwareConnected = true;
                CurrentAmplitude = meas.CurrentAmplitude ?? 0;
                ExcitationFrequency = 0; // frequency not provided by hardware yet
            }
            catch
            {
                HardwareConnected = false;
                CurrentAmplitude = 0;
                ExcitationFrequency = 0;
            }
        }

        public void OnLoadMeasurementClicked(object sender, EventArgs e)
        {
            // TODO: appropriately load measurement
            _daqService.LoadEITMeasurement("todo", DateTime.Now);
        }

        public void OnLoadMeshClicked(object sender, EventArgs e)
        {
            // TODO: Load mesh
        }

        public void OnConnectButtonClicked(object sender, EventArgs e)
        {
            if (HardwareConnected)
            {
                if (_daqService.DisconnectHardware())
                    HardwareConnected = false;
            }
            else
            {
                if (_daqService.ConnectHardware())
                {
                    HardwareConnected = true;
                    UpdateHardwareInfo();
                }
            }
        }

        [RelayCommand]
        private void SendConsoleMessage()
        {
            if (string.IsNullOrWhiteSpace(ConsoleInput))
                return;

            Workspace.AddLogMessage("Console", ConsoleInput);
            var input = ConsoleInput.Trim();
            if (input.StartsWith("/Command", StringComparison.OrdinalIgnoreCase))
            {
                var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    ExecuteCommand(parts[1], parts.Skip(2).ToArray());
                else
                    Workspace.AddErrorMessage("Invalid command format.");
            }
            ConsoleInput = string.Empty;
        }

        private readonly Dictionary<string, string> _commandDescriptions;

        private static string FormatEnumOptions<T>() where T : struct, Enum =>
            string.Join(", ", Enum.GetValues<T>().Select(v => $"{Convert.ToInt32(v)}={v}"));

        private static bool TryParseEnum<T>(string input, out T value) where T : struct, Enum
        {
            if (int.TryParse(input, out var numeric) && Enum.IsDefined(typeof(T), numeric))
            {
                value = (T)Enum.ToObject(typeof(T), numeric);
                return true;
            }
            return Enum.TryParse(input, true, out value);
        }

        private void ExecuteCommand(string command, string[] args)
        {
            switch (command.ToLower())
            {
                case "list":
                    foreach (var kv in _commandDescriptions)
                        Workspace.AddInfoMessage($"{kv.Key} - {kv.Value}");
                    break;
                case "generatemesh":
                    var mt = DiscretizationType.FEM;
                    if (args.Length > 0 && TryParseEnum(args[0], out DiscretizationType parsed))
                        mt = parsed;
                    var parameters = new DiscretizationParameters
                    {
                        MeshType = mt,
                        Layers = 2,
                        BoundaryFEMVertexCount = 16,
                        ElectrodeCount = 16,
                        Nx = 25,
                        Ny = 25,
                        Radius = 7
                    };
                    var mesh = MeshFactory.CreateDefault(parameters);
                    Workspace.SetDiscretization(mesh);
                    Workspace.AddInfoMessage($"Generated {mt} mesh.");
                    MeshUpdated?.Invoke();
                    break;
                case "connecthardware":
                    if (_daqService.ConnectHardware())
                    {
                        HardwareConnected = true;
                        UpdateHardwareInfo();
                        Workspace.AddInfoMessage("Hardware connected.");
                    }
                    else
                        Workspace.AddErrorMessage("Failed to connect hardware.");
                    break;
                case "disconnecthardware":
                    if (_daqService.DisconnectHardware())
                    {
                        HardwareConnected = false;
                        Workspace.AddInfoMessage("Hardware disconnected.");
                    }
                    else
                        Workspace.AddErrorMessage("Failed to disconnect hardware.");
                    break;
                case "initializerreconstruction":
                    var m = Workspace.GetDiscretization();
                    if (m == null)
                    {
                        Workspace.AddErrorMessage("No mesh available.");
                        break;
                    }
                    var parms = Workspace.GetReconstructionParameters();
                    _reconstructionService.InitializeReconstruction(m, parms, true);
                    Workspace.AddInfoMessage("Reconstruction initialized.");
                    break;
                case "openpage":
                    if (args.Length > 0)
                        MainThread.BeginInvokeOnMainThread(async () => await Shell.Current.GoToAsync(args[0]));
                    break;
                case "changehardwareport":
                    if (args.Length > 0 && _daqService.ChangeHardwarePort(args[0]))
                        Workspace.AddInfoMessage($"Hardware port set to {args[0]}");
                    else
                        Workspace.AddErrorMessage("Failed to change hardware port.");
                    break;
                case "setfrequency":
                    if (args.Length > 0 && double.TryParse(args[0], out var freq))
                    {
                        _daqService.SetFrequency(freq);
                        ExcitationFrequency = freq;
                        Workspace.AddInfoMessage($"Frequency set to {freq}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid frequency.");
                    break;
                case "loadmesh":
                    if (args.Length > 0)
                    {
                        try
                        {
                            IDiscretization loaded = args[0].EndsWith(".lbm", StringComparison.OrdinalIgnoreCase)
                                ? _daqService.LoadLBMGrid(args[0])
                                : _daqService.LoadFEMMesh(args[0]);
                            Workspace.SetDiscretization(loaded);
                            Workspace.AddInfoMessage($"Loaded mesh {args[0]}");
                            MeshUpdated?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            Workspace.AddErrorMessage($"Failed to load mesh: {ex.Message}");
                        }
                    }
                    break;
                case "setsolver":
                    if (args.Length > 0 && TryParseEnum(args[0], out DifferentialEquationSolver solver))
                    {
                        ReconstructionParameters.DifferentialEquationSolver = solver;
                        Workspace.AddInfoMessage($"Solver set to {solver}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid solver.");
                    break;
                case "setregularization":
                    if (args.Length > 0 && TryParseEnum(args[0], out RegularizationTechnique reg))
                    {
                        ReconstructionParameters.RegularizationTechnique = reg;
                        Workspace.AddInfoMessage($"Regularization set to {reg}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid regularization.");
                    break;
                case "seterrormetric":
                    if (args.Length > 0 && TryParseEnum(args[0], out ErrorMetric metric))
                    {
                        ReconstructionParameters.ErrorMetric = metric;
                        Workspace.AddInfoMessage($"Error metric set to {metric}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid error metric.");
                    break;
                case "setnumericoptimizer":
                    if (args.Length > 0 && TryParseEnum(args[0], out NumericOptimizer opt))
                    {
                        ReconstructionParameters.NumericOptimizer = opt;
                        Workspace.AddInfoMessage($"Numeric optimizer set to {opt}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid numeric optimizer.");
                    break;
                case "setnumericsolver":
                    if (args.Length > 0 && TryParseEnum(args[0], out NumericSolver solver2))
                    {
                        ReconstructionParameters.NumericSolver = solver2;
                        Workspace.AddInfoMessage($"Numeric solver set to {solver2}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid numeric solver.");
                    break;
                case "setstepsize":
                    if (args.Length > 0 && double.TryParse(args[0], out var step))
                    {
                        Workspace.StepSize = step;
                        Workspace.AddInfoMessage($"Step size set to {step}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid step size.");
                    break;
                case "setregularizationweight":
                    if (args.Length > 0 && double.TryParse(args[0], out var w))
                    {
                        Workspace.RegularizationWeight = w;
                        Workspace.AddInfoMessage($"Regularization weight set to {w}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid regularization weight.");
                    break;
                case "setmaxiterations":
                    if (args.Length > 0 && int.TryParse(args[0], out var mi))
                    {
                        Workspace.MaxIterationCount = mi;
                        Workspace.AddInfoMessage($"Max iterations set to {mi}");
                    }
                    else
                        Workspace.AddErrorMessage("Invalid iteration count.");
                    break;
                case "clear":
                    DebugLog.Clear();
                    break;
                default:
                    Workspace.AddWarningMessage($"Unknown command: {command}");
                    break;
            }
        }
    }
}