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
            IEnumerable<ConfigurationParameter> parameters,
            double fontSize = 13,
            double width = 214,
            double height = 80,
            double rotation = 0)
        {
            Id = id;
            Title = title;
            Type = type;
            IconColor = iconColor;
            X = x;
            Y = y;
            FontSize = fontSize;
            Width = width;
            Height = height;
            Rotation = rotation;
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

        private double _width = 214;
        /// <summary>
        /// Visual width of the block on the canvas.
        /// </summary>
        public double Width { get => _width; set => SetProperty(ref _width, value); }

        private double _height = 80;
        /// <summary>
        /// Visual height of the block on the canvas.
        /// </summary>
        public double Height { get => _height; set => SetProperty(ref _height, value); }

        private double _fontSize = 13;
        /// <summary>
        /// Font size used for block labels.
        /// </summary>
        public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, value); }

        private double _rotation;
        /// <summary>
        /// Rotation angle in degrees applied to the block container.
        /// </summary>
        public double Rotation { get => _rotation; set => SetProperty(ref _rotation, value % 360); }

        public event Action<ReconstructionConfigurationBlock>? ParametersChanged;

        private void HookParameterChanges()
        {
            void Attach(ConfigurationParameter p)
            {
                switch (p)
                {
                    case ChoiceParameter choice:
                        choice.PropertyChanged += (_, args) =>
                        {
                            if (args.PropertyName == nameof(ChoiceParameter.SelectedOption) || args.PropertyName == nameof(ChoiceParameter.Options))
                                UpdateHighlightedOption();
                            ParametersChanged?.Invoke(this);
                        };
                        break;
                    case NumberParameter number:
                        number.PropertyChanged += (_, __) => ParametersChanged?.Invoke(this);
                        break;
                    case BoolParameter toggle:
                        toggle.PropertyChanged += (_, __) => ParametersChanged?.Invoke(this);
                        break;
                    case TextParameter text:
                        text.PropertyChanged += (_, __) => ParametersChanged?.Invoke(this);
                        break;
                }
            }

            foreach (var p in Parameters)
                Attach(p);

            // Rehook if collection ever changes (future extension possibility)
            Parameters.CollectionChanged += (_, __) =>
            {
                foreach (var p in Parameters)
                    Attach(p);
                UpdateHighlightedOption();
                ParametersChanged?.Invoke(this);
            };
        }

        private void UpdateHighlightedOption()
        {
            HighlightedOption = Parameters.OfType<ChoiceParameter>().FirstOrDefault()?.SelectedOption ?? Type.ToString();
        }
    }
}
