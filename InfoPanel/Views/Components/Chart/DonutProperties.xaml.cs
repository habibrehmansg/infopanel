using InfoPanel.Models;
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
    /// Interaction logic for DonutProperties.xaml
    /// </summary>
    public partial class DonutProperties : UserControl
    {
        public static readonly DependencyProperty ItemProperty =
      DependencyProperty.Register("DonutDisplayItem", typeof(DonutDisplayItem), typeof(DonutProperties));

        public DonutDisplayItem DonutDisplayItem
        {
            get { return (DonutDisplayItem)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public DonutProperties()
        {
            InitializeComponent();
        }
    }
}
