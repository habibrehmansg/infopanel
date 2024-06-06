
using System.Windows;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for DesignPage.xaml
    /// </summary>
    public partial class DesignPage
    {
        public DesignPage()
        {
            InitializeComponent();

            Unloaded += DesignPage_Unloaded;
        }

        private void DesignPage_Unloaded(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.SelectedItem = null;
        }
    }
}
