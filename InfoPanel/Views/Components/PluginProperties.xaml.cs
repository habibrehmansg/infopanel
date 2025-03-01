using InfoPanel.Models;
using InfoPanel.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for PluginProperties.xaml
    /// </summary>
    public partial class PluginProperties : UserControl
    {
        public static readonly DependencyProperty ItemProperty =
     DependencyProperty.Register("PluginDisplayModel", typeof(PluginDisplayModel), typeof(PluginProperties));

        public PluginDisplayModel PluginDisplayModel
        {
            get { return (PluginDisplayModel)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public PluginProperties()
        {
            InitializeComponent();
        }
    }
}
