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

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
