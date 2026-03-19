using InfoPanel.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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
            Loaded += OnLoaded;
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LogsViewModel.LogText))
                {
                    Dispatcher.BeginInvoke(() => LogScrollViewer.ScrollToEnd(), DispatcherPriority.Loaded);
                }
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.LoadLogsCommand.Execute(null);
        }
    }
}
