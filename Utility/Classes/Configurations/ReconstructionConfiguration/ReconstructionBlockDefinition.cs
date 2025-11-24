using System;
using System.Collections.Generic;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Configurations.ReconstructionConfiguration
{
    /// <summary>
    /// Describes a reconstruction block option exposed in the UI and the
    /// corresponding application logic to apply the selected values.
    /// </summary>
    public class ReconstructionBlockDefinition
    {
        public ReconstructionBlockDefinition(
            BlockType type,
            string title,
            string iconColor,
            Func<IEnumerable<ConfigurationParameter>> parameterFactory,
            Action<ReconstructionConfigurationBlock, EITReconstructionParameters>? applyParameters = null)
        {
            Type = type;
            Title = title;
            IconColor = iconColor;
            ParameterFactory = parameterFactory;
            ApplyParameters = applyParameters ?? ((_, _) => { });
        }

        public BlockType Type { get; }
        public string Title { get; }
        public string IconColor { get; }
        public Func<IEnumerable<ConfigurationParameter>> ParameterFactory { get; }
        public Action<ReconstructionConfigurationBlock, EITReconstructionParameters> ApplyParameters { get; }
    }
}
