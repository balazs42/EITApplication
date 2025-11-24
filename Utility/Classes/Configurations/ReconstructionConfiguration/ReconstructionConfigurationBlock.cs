using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Represents a node (block) in the reconstruction configuration graph.
    /// Stores visual properties and a collection of specific parameters.
    /// </summary>
    public class ReconstructionConfigurationBlock : ObservableObject
    {
        public ReconstructionConfigurationBlock(
            string id,
            string title,
            BlockType type,
            string iconColor,
            double x,
            double y,
            IEnumerable<ConfigurationParameter> parameters)
        {
            Id = id;
            Title = title;
            Type = type;
            IconColor = iconColor;
            X = x;
            Y = y;
            Parameters = new ObservableCollection<ConfigurationParameter>(parameters);
            HookParameterChanges();
            UpdateHighlightedOption();
        }

        /// <summary>
        /// Unique identifier for the block instance.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// The display title of the block.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// The functional type of the block.
        /// </summary>
        public BlockType Type { get; set; }

        /// <summary>
        /// Hex color code for the block's UI representation.
        /// </summary>
        public string IconColor { get; set; }

        private string _highlightedOption = string.Empty;
        /// <summary>
        /// Short text shown on the block card to reflect the key selection (e.g., chosen solver).
        /// </summary>
        public string HighlightedOption { get => _highlightedOption; set => SetProperty(ref _highlightedOption, value); }

        private double _x;
        /// <summary>
        /// X coordinate on the canvas.
        /// </summary>
        public double X { get => _x; set => SetProperty(ref _x, value); }

        private double _y;
        /// <summary>
        /// Y coordinate on the canvas.
        /// </summary>
        public double Y { get => _y; set => SetProperty(ref _y, value); }

        private bool _isSelected;
        /// <summary>
        /// Indicates whether the block is currently selected in the editor.
        /// </summary>
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

        /// <summary>
        /// Collection of configurable parameters associated with this block.
        /// </summary>
        public ObservableCollection<ConfigurationParameter> Parameters { get; set; } = new();

        public event Action<ReconstructionConfigurationBlock>? ParametersChanged;

        private void HookParameterChanges()
        {
            foreach (var choice in Parameters.OfType<ChoiceParameter>())
            {
                choice.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ChoiceParameter.SelectedOption))
                        UpdateHighlightedOption();

                    ParametersChanged?.Invoke(this);
                };
            }

            foreach (var number in Parameters.OfType<NumberParameter>())
            {
                number.PropertyChanged += (_, __) => ParametersChanged?.Invoke(this);
            }

            foreach (var toggle in Parameters.OfType<BoolParameter>())
            {
                toggle.PropertyChanged += (_, __) => ParametersChanged?.Invoke(this);
            }

            foreach (var text in Parameters.OfType<TextParameter>())
            {
                text.PropertyChanged += (_, __) => ParametersChanged?.Invoke(this);
            }
        }

        private void UpdateHighlightedOption()
        {
            HighlightedOption = Parameters.OfType<ChoiceParameter>().FirstOrDefault()?.SelectedOption ?? Type.ToString();
        }
    }
}
