using InfoPanel.ViewModels;
using System.Windows.Controls;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for AboutPage.xaml
    /// </summary>
    public partial class PluginsPage : Page
    {
        public PluginsViewModel ViewModel
        {
            get;
        }

        public PluginsPage(PluginsViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
        }
    }
}
