using InfoPanel.ViewModels;
using System.Windows.Controls;

namespace InfoPanel.Views.Pages
{
    public partial class LogsPage : Page
    {
        public LogsViewModel ViewModel { get; }

        public LogsPage(LogsViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;
            InitializeComponent();
        }
    }
}
