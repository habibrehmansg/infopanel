using InfoPanel.ViewModels;
using InfoPanel.Views.Components;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Pages
{
    public partial class PluginsPage : Page
    {
        public PluginsViewModel ViewModel { get; }
        public PluginBrowserViewModel BrowserViewModel { get; }

        private readonly IContentDialogService _contentDialogService;
        private readonly AccountViewModel? _accountVm;

        public PluginsPage(PluginsViewModel viewModel, PluginBrowserViewModel browserViewModel)
        {
            ViewModel = viewModel;
            BrowserViewModel = browserViewModel;
            DataContext = viewModel;

            _contentDialogService = App.GetService<IContentDialogService>()
                ?? throw new System.InvalidOperationException("ContentDialogService is not registered.");
            _accountVm = App.GetService<AccountViewModel>();

            InitializeComponent();

            BrowserControl.DataContext = BrowserViewModel;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Start();

            if (_accountVm != null)
            {
                _accountVm.PropertyChanged += OnAccountPropertyChanged;
                BrowserViewModel.IsLoggedIn = _accountVm.IsLoggedIn;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Stop();

            if (_accountVm != null)
            {
                _accountVm.PropertyChanged -= OnAccountPropertyChanged;
            }
        }

        private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountViewModel.IsLoggedIn))
            {
                BrowserViewModel.IsLoggedIn = _accountVm?.IsLoggedIn ?? false;
            }
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] == DiscoverTab)
            {
                BrowserViewModel.IsLoggedIn = _accountVm?.IsLoggedIn ?? false;
            }
        }

        internal async void ShowPluginDetail(PluginBrowserItemViewModel item)
        {
            var detailVm = new PluginDetailViewModel();
            var detailControl = new PluginDetailControl
            {
                DataContext = detailVm
            };

            var dialog = new ContentDialog
            {
                Title = item.Name,
                Content = detailControl,
                CloseButtonText = "Close",
                CloseButtonAppearance = ControlAppearance.Secondary
            };

            _ = detailVm.LoadDetailAsync(item.Slug, item.IsInstalled, item.IsUpdateAvailable);

            detailVm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PluginDetailViewModel.IsInstalled))
                {
                    item.IsInstalled = detailVm.IsInstalled;
                    item.IsUpdateAvailable = detailVm.IsUpdateAvailable;
                }
            };

            await _contentDialogService.ShowAsync(dialog, CancellationToken.None);
        }
    }
}
