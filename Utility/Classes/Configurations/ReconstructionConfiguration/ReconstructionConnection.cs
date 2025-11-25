using CommunityToolkit.Mvvm.ComponentModel;

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

        private double _weight = 1.0;
        /// <summary>
        /// Relative influence of this connection when multiple blocks of the
        /// same category are present.
        /// </summary>
        public double Weight
        {
            get => _weight;
            set => SetProperty(ref _weight, value);
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
