using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.ApiClient;
using InfoPanel.Monitors;
using InfoPanel.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.ViewModels
{
    public partial class PluginBrowserViewModel : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<PluginBrowserViewModel>();

        private int _currentPage = 1;
        private int _totalPages = 1;
        private CancellationTokenSource? _searchCts;
        private Dictionary<string, string?> _installedPlugins = new(StringComparer.OrdinalIgnoreCase);
        private bool _hasLoaded;

        public ObservableCollection<PluginBrowserItemViewModel> Plugins { get; } = [];

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private Category? _selectedCategory;

        [ObservableProperty]
        private Sort _selectedSort = Sort.Downloads;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _hasMorePages;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private bool _isLoggedIn;

        partial void OnSearchTextChanged(string value)
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300, token);
                    if (!token.IsCancellationRequested)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => RefreshCommand.Execute(null));
                    }
                }
                catch (TaskCanceledException) { }
            });
        }

        partial void OnSelectedCategoryChanged(Category? value)
        {
            _ = RefreshAsync();
        }

        partial void OnSelectedSortChanged(Sort value)
        {
            _ = RefreshAsync();
        }

        /// <summary>
        /// Called when the Discover tab is first selected. Waits for PluginMonitor
        /// to finish loading before refreshing so the installed map is accurate.
        /// </summary>
        public async Task EnsureLoadedAsync()
        {
            if (_hasLoaded) return;
            _hasLoaded = true;

            // Wait for PluginMonitor to finish discovering plugins (fire-and-forget startup)
            for (int i = 0; i < 20; i++)
            {
                lock (PluginMonitor.Instance.PluginsLock)
                {
                    if (PluginMonitor.Instance.Plugins.Count > 0) break;
                }
                await Task.Delay(250);
            }

            await RefreshAsync();
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            _currentPage = 1;
            Plugins.Clear();
            ErrorMessage = null;
            BuildInstalledPluginsMap();
            await LoadPageAsync();
        }

        [RelayCommand]
        public async Task LoadMoreAsync()
        {
            if (_currentPage < _totalPages)
            {
                _currentPage++;
                await LoadPageAsync();
            }
        }

        private async Task LoadPageAsync()
        {
            IsLoading = true;
            ErrorMessage = null;

            try
            {
                var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;
                var response = await InfoPanelApiService.Instance.Client.Get_ListPluginsAsync(
                    page: _currentPage,
                    pageSize: 20,
                    search: search,
                    category: SelectedCategory,
                    sort: SelectedSort);

                if (response?.Data != null)
                {
                    foreach (var item in response.Data)
                    {
                        Plugins.Add(new PluginBrowserItemViewModel(item, _installedPlugins));
                    }
                }

                if (response?.Pagination != null)
                {
                    _totalPages = (int)response.Pagination.TotalPages;
                }

                HasMorePages = _currentPage < _totalPages;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load plugins from API");
                ErrorMessage = "Failed to load plugins. Please check your internet connection.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void BuildInstalledPluginsMap()
        {
            _installedPlugins = new(StringComparer.OrdinalIgnoreCase);

            lock (PluginMonitor.Instance.PluginsLock)
            {
                foreach (var descriptor in PluginMonitor.Instance.Plugins)
                {
                    var folderName = descriptor.FolderName;
                    if (folderName != null)
                    {
                        _installedPlugins.TryAdd(folderName, descriptor.PluginInfo?.Version);
                    }
                }
            }
        }
    }
}
