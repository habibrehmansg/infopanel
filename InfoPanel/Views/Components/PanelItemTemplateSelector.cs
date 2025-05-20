using InfoPanel.Models;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Components
{
    public class PanelItemTemplateSelector: DataTemplateSelector
    {
        public required DataTemplate SimpleTemplate { get; set; }

        public required DataTemplate GroupTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            return item switch
            {
                GroupDisplayItem => GroupTemplate,
                _ => SimpleTemplate
            };
        }
    }
}
