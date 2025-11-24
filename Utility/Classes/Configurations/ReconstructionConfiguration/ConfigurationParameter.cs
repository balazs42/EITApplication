using CommunityToolkit.Mvvm.ComponentModel;

namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Base class for all configuration parameters displayed in the properties panel.
    /// </summary>
    public abstract class ConfigurationParameter : ObservableObject
    {
        /// <summary>
        /// Gets or sets the display name of the parameter.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the unique key used for serialization and logic mapping.
        /// </summary>
        public string Key { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a simple text input parameter.
    /// </summary>
    public class TextParameter : ConfigurationParameter
    {
        private string _value = string.Empty;
        /// <summary>
        /// Gets or sets the text value.
        /// </summary>
        public string Value { get => _value; set => SetProperty(ref _value, value); }
    }

    /// <summary>
    /// Represents a numeric input parameter with optional constraints.
    /// </summary>
    public class NumberParameter : ConfigurationParameter
    {
        private double _value;
        /// <summary>
        /// Gets or sets the numeric value.
        /// </summary>
        public double Value { get => _value; set => SetProperty(ref _value, value); }

        /// <summary>
        /// Minimum allowed value.
        /// </summary>
        public double Min { get; set; } = double.MinValue;

        /// <summary>
        /// Maximum allowed value.
        /// </summary>
        public double Max { get; set; } = double.MaxValue;

        /// <summary>
        /// Suggested increment step for UI controls.
        /// </summary>
        public double Step { get; set; } = 1.0;
    }

    /// <summary>
    /// Represents a boolean toggle parameter.
    /// </summary>
    public class BoolParameter : ConfigurationParameter
    {
        private bool _value;
        /// <summary>
        /// Gets or sets the boolean state.
        /// </summary>
        public bool Value { get => _value; set => SetProperty(ref _value, value); }
    }

    /// <summary>
    /// Represents a selection parameter from a predefined list of options.
    /// </summary>
    public class ChoiceParameter : ConfigurationParameter
    {
        /// <summary>
        /// List of available options.
        /// </summary>
        public List<string> Options { get; set; } = [];

        private string _selectedOption = string.Empty;
        /// <summary>
        /// Gets or sets the currently selected option.
        /// </summary>
        public string SelectedOption { get => _selectedOption; set => SetProperty(ref _selectedOption, value); }
    }
}
