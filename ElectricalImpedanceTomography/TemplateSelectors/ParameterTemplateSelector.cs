using Microsoft.Maui.Controls;
using Utility.Classes.Configurations.ReconstructionConfiguration;

namespace ElectricalImpedanceTomography.TemplateSelectors
{
    public class ParameterTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? TextTemplate { get; set; }
        public DataTemplate? NumberTemplate { get; set; }
        public DataTemplate? BoolTemplate { get; set; }
        public DataTemplate? ChoiceTemplate { get; set; }

        protected override DataTemplate? OnSelectTemplate(object item, BindableObject container)
        {
            return item switch
            {
                TextParameter => TextTemplate!,
                NumberParameter => NumberTemplate!,
                BoolParameter => BoolTemplate!,
                ChoiceParameter => ChoiceTemplate!,
                _ => null
            };
        }
    }
}
