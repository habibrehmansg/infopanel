using InfoPanel.Models;
using System.Collections.ObjectModel;
using System.Drawing.Text;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for TextProperties.xaml
    /// </summary>
    /// 


    public partial class TextProperties : UserControl
    {
        public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register("TextDisplayItem", typeof(TextDisplayItem), typeof(TextProperties));

        public ObservableCollection<string> InstalledFonts { get; } = new ObservableCollection<string>();

        public TextDisplayItem TextDisplayItem
        {
            get { return (TextDisplayItem)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public TextProperties()
        {
            InitializeComponent();
            FetchInstalledFontNames();
        }

        private void FetchInstalledFontNames()
        {
            InstalledFontCollection installedFonts = new InstalledFontCollection();
            foreach (var font in installedFonts.Families.Select(f => f.Name))
            {
                InstalledFonts.Add(font);
            }
        }

        private void NumberBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if(TextDisplayItem == null)
            {
                return;
            }

            var numBox = ((NumberBox)sender);
            double newValue;
            if (double.TryParse(numBox.Text, out newValue))
            {
                numBox.Value = newValue;
                TextDisplayItem.FontSize = (int)newValue;
            }
        }
    }
}
