using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Represents a connection between two blocks.
    /// Inherits ObservableObject to support selection binding.
    /// </summary>
    public class ReconstructionConnection : ObservableObject
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public ReconstructionConfigurationBlock Source { get; set; }
        public ReconstructionConfigurationBlock Target { get; set; }

        private const double DefaultControlOffset = 60.0;

        private double _weight = 1.0;
        /// <summary>
        /// Relative influence of this connection when multiple blocks of the
        /// same category are present.
        /// </summary>
        public double Weight
        {
            get => _weight;
            set
            {
                var clamped = Math.Clamp(value, 0.0, 1.0);
                clamped = Math.Round(clamped, 4);
                SetProperty(ref _weight, clamped);
            }
        }

        private double _controlOffset1X = DefaultControlOffset;
        public double ControlOffset1X
        {
            get => _controlOffset1X;
            set => SetProperty(ref _controlOffset1X, value);
        }

        private double _controlOffset1Y;
        public double ControlOffset1Y
        {
            get => _controlOffset1Y;
            set => SetProperty(ref _controlOffset1Y, value);
        }

        private double _controlOffset2X = -DefaultControlOffset;
        public double ControlOffset2X
        {
            get => _controlOffset2X;
            set => SetProperty(ref _controlOffset2X, value);
        }

        private double _controlOffset2Y;
        public double ControlOffset2Y
        {
            get => _controlOffset2Y;
            set => SetProperty(ref _controlOffset2Y, value);
        }

        private bool _requiresWeight;
        /// <summary>
        /// Indicates whether the connection should display and enforce a
        /// weight (e.g., when multiple regularizers are active).
        /// </summary>
        public bool RequiresWeight
        {
            get => _requiresWeight;
            set => SetProperty(ref _requiresWeight, value);
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
