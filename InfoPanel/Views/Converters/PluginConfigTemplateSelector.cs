using InfoPanel.Plugins;
using InfoPanel.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Converters
{
    public class PluginConfigTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? StringTemplate { get; set; }
        public DataTemplate? IntegerTemplate { get; set; }
        public DataTemplate? DoubleTemplate { get; set; }
        public DataTemplate? BooleanTemplate { get; set; }
        public DataTemplate? ChoiceTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is PluginConfigPropertyViewModel vm)
            {
                return vm.Type switch
                {
                    PluginConfigType.String => StringTemplate,
                    PluginConfigType.Integer => IntegerTemplate,
                    PluginConfigType.Double => DoubleTemplate,
                    PluginConfigType.Boolean => BooleanTemplate,
                    PluginConfigType.Choice => ChoiceTemplate,
                    _ => StringTemplate
                };
            }
            return base.SelectTemplate(item, container);
        }
    }
}
