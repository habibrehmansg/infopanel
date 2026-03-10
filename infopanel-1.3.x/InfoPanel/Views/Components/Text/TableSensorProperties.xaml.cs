using InfoPanel.Models;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for TextProperties.xaml
    /// </summary>
    /// 


    public partial class TableSensorProperties : UserControl
    {
        public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register("TableSensorDisplayItem", typeof(TableSensorDisplayItem), typeof(TableSensorProperties));


        public TableSensorDisplayItem TableSensorDisplayItem
        {
            get { return (TableSensorDisplayItem)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public TableSensorProperties()
        {
            InitializeComponent();
        }
    }
}
