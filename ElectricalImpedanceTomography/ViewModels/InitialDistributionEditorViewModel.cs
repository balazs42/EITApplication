using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer;
using Utility.Classes.Factories;
using Utility.Classes.Reconstruction;

namespace ElectricalImpedanceTomography.ViewModels
{
    public enum InitialDistributionEditorMode
    {
        Homogeneous,
        Random,
        CloseToTarget
    }

    public partial class InitialDistributionEditorViewModel : ObservableObject
    {
        private readonly Random _random = new();

        private IDiscretization? _discretization;
        private ConductivityDistribution? _originalDistribution;
        private ConductivityDistribution? _currentDistribution;
        private ConductivityDistribution? _initialDistribution;

        private bool _suppressUpdates;
        private bool _isInitialized;

        public IReadOnlyList<InitialDistributionEditorMode> AvailableModes { get; }
            = Enum.GetValues<InitialDistributionEditorMode>();

        [ObservableProperty]
        private InitialDistributionEditorMode selectedMode = InitialDistributionEditorMode.Homogeneous;

        [ObservableProperty]
        private double backgroundConductivity;

        [ObservableProperty]
        private double randomPercent = 10.0;

        [ObservableProperty]
        private double distortionStrength = 10.0;

        public bool IsRandomMode => SelectedMode == InitialDistributionEditorMode.Random;

        public bool IsCloseToTargetMode => SelectedMode == InitialDistributionEditorMode.CloseToTarget;

        public string RandomPercentLabel => $"{Math.Round(RandomPercent)}% of elements randomized";

        public string DistortionLabel => $"{Math.Round(DistortionStrength)}% distortion";

        public ConductivityDistribution? CurrentDistribution => _currentDistribution;

        public IDiscretization? Discretization => _discretization;

        public event EventHandler? DistributionUpdated;

        public void Initialize(IDiscretization discretization,
                               ConductivityDistribution initialDistribution,
                               ConductivityDistribution? originalDistribution,
                               InitialDistributionTypes initialType)
        {
            _discretization = discretization ?? throw new ArgumentNullException(nameof(discretization));
            _originalDistribution = originalDistribution != null
                ? new ConductivityDistribution(originalDistribution.Conductivities)
                : null;

            _initialDistribution = new ConductivityDistribution(initialDistribution.Conductivities);
            _currentDistribution = new ConductivityDistribution(initialDistribution.Conductivities);

            double minBound = ConductivityClipper.MinimumBound;
            double maxBound = ConductivityClipper.MaximumBound;
            double average = initialDistribution.Conductivities.Count > 0
                ? initialDistribution.Conductivities.Values.Average()
                : Math.Clamp(1.0, minBound, maxBound);
            average = Math.Clamp(average, minBound, maxBound);

            _suppressUpdates = true;
            BackgroundConductivity = average;
            RandomPercent = 10.0;
            DistortionStrength = 10.0;
            SelectedMode = MapInitialTypeToMode(initialType);
            _suppressUpdates = false;

            _isInitialized = true;

            OnPropertyChanged(nameof(IsRandomMode));
            OnPropertyChanged(nameof(IsCloseToTargetMode));
            OnPropertyChanged(nameof(RandomPercentLabel));
            OnPropertyChanged(nameof(DistortionLabel));
            OnPropertyChanged(nameof(CurrentDistribution));

            RaiseDistributionUpdated();
        }

        partial void OnSelectedModeChanged(InitialDistributionEditorMode value)
        {
            if (!_isInitialized || _suppressUpdates)
                return;

            OnPropertyChanged(nameof(IsRandomMode));
            OnPropertyChanged(nameof(IsCloseToTargetMode));

            UpdateDistribution();
        }

        partial void OnBackgroundConductivityChanged(double value)
        {
            if (!_isInitialized || _suppressUpdates)
                return;

            if (!double.IsFinite(value))
            {
                SetBackgroundConductivity(Math.Clamp(ConductivityClipper.MinimumBound,
                                                     ConductivityClipper.MinimumBound,
                                                     ConductivityClipper.MaximumBound));
                return;
            }

            double min = ConductivityClipper.MinimumBound;
            double max = ConductivityClipper.MaximumBound;
            double sanitized = Math.Clamp(value, min, max);

            if (Math.Abs(sanitized - value) > double.Epsilon)
            {
                SetBackgroundConductivity(sanitized);
                return;
            }

            UpdateDistribution();
        }

        partial void OnRandomPercentChanged(double value)
        {
            if (!_isInitialized || _suppressUpdates)
                return;

            double sanitized = Math.Clamp(value, 0.0, 100.0);
            if (Math.Abs(sanitized - value) > double.Epsilon)
            {
                SetRandomPercent(sanitized);
                return;
            }

            OnPropertyChanged(nameof(RandomPercentLabel));
            UpdateDistribution();
        }

        partial void OnDistortionStrengthChanged(double value)
        {
            if (!_isInitialized || _suppressUpdates)
                return;

            double sanitized = Math.Clamp(value, 0.0, 100.0);
            if (Math.Abs(sanitized - value) > double.Epsilon)
            {
                SetDistortionStrength(sanitized);
                return;
            }

            OnPropertyChanged(nameof(DistortionLabel));
            UpdateDistribution();
        }

        private void SetBackgroundConductivity(double value)
        {
            _suppressUpdates = true;
            BackgroundConductivity = value;
            _suppressUpdates = false;

            if (_isInitialized)
                UpdateDistribution();
        }

        private void SetRandomPercent(double value)
        {
            _suppressUpdates = true;
            RandomPercent = value;
            _suppressUpdates = false;
            OnPropertyChanged(nameof(RandomPercentLabel));

            if (_isInitialized)
                UpdateDistribution();
        }

        private void SetDistortionStrength(double value)
        {
            _suppressUpdates = true;
            DistortionStrength = value;
            _suppressUpdates = false;
            OnPropertyChanged(nameof(DistortionLabel));

            if (_isInitialized)
                UpdateDistribution();
        }

        private void UpdateDistribution()
        {
            if (!_isInitialized || _suppressUpdates)
                return;

            if (_discretization == null)
                return;

            var updated = GenerateDistribution();
            updated = ConductivityClipper.Clip(updated);

            _currentDistribution = new ConductivityDistribution(updated.Conductivities);

            _discretization.SetConductivityDistribution(new ConductivityDistribution(_currentDistribution.Conductivities));
            Workspace.SetInitialConductivityDistribution(new ConductivityDistribution(_currentDistribution.Conductivities));
            Workspace.SetInitialDiscretization(_discretization.DeepCopy());

            var parameters = Workspace.GetReconstructionParameters();
            parameters.InitialDistributionType = MapModeToInitialType(SelectedMode);

            OnPropertyChanged(nameof(CurrentDistribution));
            RaiseDistributionUpdated();
        }

        private ConductivityDistribution GenerateDistribution()
        {
            if (_discretization == null)
                throw new InvalidOperationException("Discretization is not available.");

            var elements = _discretization.GetElements();
            var values = new Dictionary<int, double>(elements.Count);
            double minBound = ConductivityClipper.MinimumBound;
            double maxBound = ConductivityClipper.MaximumBound;

            switch (SelectedMode)
            {
                case InitialDistributionEditorMode.Random:
                    foreach (var element in elements)
                        values[element.Id] = BackgroundConductivity;

                    int totalCount = elements.Count;
                    if (totalCount > 0)
                    {
                        double ratio = Math.Clamp(RandomPercent / 100.0, 0.0, 1.0);
                        int targetCount = (int)Math.Round(totalCount * ratio);
                        if (ratio > 0.0 && targetCount == 0)
                            targetCount = 1;

                        if (targetCount > 0)
                        {
                            var shuffledIds = elements.Select(e => e.Id)
                                                       .OrderBy(_ => _random.NextDouble())
                                                       .Take(targetCount);

                            foreach (var id in shuffledIds)
                            {
                                double randomValue = minBound + _random.NextDouble() * (maxBound - minBound);
                                values[id] = randomValue;
                            }
                        }
                    }
                    break;

                case InitialDistributionEditorMode.CloseToTarget:
                    var source = _originalDistribution
                                 ?? _initialDistribution
                                 ?? _currentDistribution;
                    double distortion = Math.Clamp(DistortionStrength / 100.0, 0.0, 1.0);

                    foreach (var element in elements)
                    {
                        double baseValue = BackgroundConductivity;
                        if (source != null && source.Conductivities.TryGetValue(element.Id, out double existing))
                            baseValue = existing;

                        double noise = (_random.NextDouble() * 2.0 - 1.0) * distortion;
                        double perturbed = baseValue * (1.0 + noise);
                        values[element.Id] = perturbed;
                    }
                    break;

                case InitialDistributionEditorMode.Homogeneous:
                default:
                    foreach (var element in elements)
                        values[element.Id] = BackgroundConductivity;
                    break;
            }

            return new ConductivityDistribution(values);
        }

        private static InitialDistributionEditorMode MapInitialTypeToMode(InitialDistributionTypes type)
            => type switch
            {
                InitialDistributionTypes.Random => InitialDistributionEditorMode.Random,
                InitialDistributionTypes.CloseToTarget => InitialDistributionEditorMode.CloseToTarget,
                _ => InitialDistributionEditorMode.Homogeneous
            };

        private static InitialDistributionTypes MapModeToInitialType(InitialDistributionEditorMode mode)
            => mode switch
            {
                InitialDistributionEditorMode.Random => InitialDistributionTypes.Random,
                InitialDistributionEditorMode.CloseToTarget => InitialDistributionTypes.CloseToTarget,
                _ => InitialDistributionTypes.Homogeneous
            };

        private void RaiseDistributionUpdated()
        {
            DistributionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }
}
