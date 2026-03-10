using InfoPanel.Drawing;
using InfoPanel.Models;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using static InfoPanel.Models.ShapeDisplayItem;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for ShapeProperties.xaml
    /// </summary>
    public partial class ShapeProperties : UserControl
    {
        public static readonly DependencyProperty ItemProperty =
     DependencyProperty.Register("ShapeDisplayItem", typeof(ShapeDisplayItem), typeof(ShapeProperties));

        public ShapeDisplayItem ShapeDisplayItem
        {
            get { return (ShapeDisplayItem)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public ShapeProperties()
        {
            InitializeComponent();
            ComboBoxType.ItemsSource = Enum.GetValues(typeof(ShapeType)).Cast<ShapeType>();
            ComboBoxGradientType.ItemsSource = Enum.GetValues(typeof(GradientType)).Cast<GradientType>();
        }
    }
}
