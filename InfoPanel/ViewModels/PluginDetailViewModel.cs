using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.ApiClient;
using InfoPanel.Monitors;
using InfoPanel.Services;
using InfoPanel.Utils;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace InfoPanel.ViewModels
{
    public partial class PluginDetailViewModel : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<PluginDetailViewModel>();
        private static readonly HttpClient HttpClient = new();

        [ObservableProperty]
        private string _slug = string.Empty;

        [ObservableProperty]
        private string _repoName = string.Empty;

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _description = string.Empty;

        [ObservableProperty]
        private string _icon = string.Empty;

        [ObservableProperty]
        private string _author = string.Empty;

        [ObservableProperty]
        private string _authorAvatar = string.Empty;

        [ObservableProperty]
        private string _category = string.Empty;

        [ObservableProperty]
        private string? _latestVersion;

        [ObservableProperty]
        private double _downloads;

        [ObservableProperty]
        private double _averageRating;

        [ObservableProperty]
        private double _ratingCount;

        [ObservableProperty]
        private string _sourceUrl = string.Empty;

        [ObservableProperty]
        private double? _userRating;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _isDownloading;

        [ObservableProperty]
        private bool _isInstalled;

        [ObservableProperty]
        private bool _isUpdateAvailable;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private string? _latestReleaseVersion;

        [ObservableProperty]
        private string? _latestReleaseDate;

        public ObservableCollection<string> ChangelogItems { get; } = [];

        public ObservableCollection<ReleaseViewModel> Releases { get; } = [];

        public async Task LoadDetailAsync(string slug, bool isInstalled, bool isUpdateAvailable)
        {
            IsLoading = true;
            ErrorMessage = null;
            IsInstalled = isInstalled;
            IsUpdateAvailable = isUpdateAvailable;

            try
            {
                var response = await InfoPanelApiService.Instance.Client.Get_GetPluginAsync(slug);
                var data = response?.Data;

                if (data != null)
                {
                    Slug = data.Slug;
                    RepoName = data.RepoName;
                    Name = data.Name;
                    Description = data.Description;
                    Icon = data.Icon;
                    Author = data.Author;
                    AuthorAvatar = data.AuthorAvatar;
                    Category = data.Category;
                    LatestVersion = data.LatestVersion;
                    Downloads = data.Downloads;
                    AverageRating = data.AverageRating;
                    RatingCount = data.RatingCount;
                    SourceUrl = data.SourceUrl;
                    UserRating = data.UserRating;

                    ChangelogItems.Clear();
                    Releases.Clear();

                    if (data.Releases != null && data.Releases.Count > 0)
                    {
                        var releasesList = new System.Collections.Generic.List<ApiClient.Releases>(data.Releases);

                        // First release = "What's New"
                        LatestReleaseVersion = releasesList[0].Version;
                        LatestReleaseDate = releasesList[0].PublishedAt;
                        if (releasesList[0].ChangelogItems != null)
                        {
                            foreach (var item in releasesList[0].ChangelogItems)
                            {
                                ChangelogItems.Add(item);
                            }
                        }

                        // Remaining releases = version history
                        for (int i = 1; i < releasesList.Count; i++)
                        {
                            Releases.Add(new ReleaseViewModel(releasesList[i]));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load plugin detail for {Slug}", slug);
                ErrorMessage = "Failed to load plugin details.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task<bool> DownloadAsync()
        {
            var accountVm = App.GetService<AccountViewModel>();
            if (accountVm == null || !accountVm.IsLoggedIn)
            {
                ErrorMessage = "Please sign in to download plugins.";
                return false;
            }

            IsDownloading = true;
            ErrorMessage = null;

            try
            {
                var response = await InfoPanelApiService.Instance.Client.Post_RecordPluginDownloadAsync(Slug);

                if (response?.DownloadUrl == null)
                {
                    ErrorMessage = "No download URL available.";
                    return false;
                }

                Downloads = response.Downloads;

                var zipBytes = await HttpClient.GetByteArrayAsync(response.DownloadUrl);
                var pluginFolder = FileUtil.GetExternalPluginFolder();
                var zipPath = Path.Combine(pluginFolder, $"{Slug}.zip");
                await File.WriteAllBytesAsync(zipPath, zipBytes);

                await PluginMonitor.Instance.InstallPluginFromZipAsync(zipPath);

                IsInstalled = true;
                IsUpdateAvailable = false;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to download plugin {Slug}", Slug);
                ErrorMessage = "Download failed. Please try again.";
                return false;
            }
            finally
            {
                IsDownloading = false;
            }
        }

        [RelayCommand]
        public async Task UninstallAsync()
        {
            IsDownloading = true;
            ErrorMessage = null;

            try
            {
                Plugins.Loader.PluginDescriptor? descriptor;
                lock (PluginMonitor.Instance.PluginsLock)
                {
                    descriptor = PluginMonitor.Instance.Plugins.FirstOrDefault(
                        p => string.Equals(p.FolderName, RepoName, StringComparison.OrdinalIgnoreCase));
                }

                if (descriptor == null)
                {
                    ErrorMessage = "Plugin not found locally.";
                    return;
                }

                await PluginMonitor.Instance.UninstallPluginAsync(descriptor);

                IsInstalled = false;
                IsUpdateAvailable = false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to uninstall plugin {Slug}", Slug);
                ErrorMessage = "Uninstall failed. Please try again.";
            }
            finally
            {
                IsDownloading = false;
            }
        }

        [RelayCommand]
        public async Task RateAsync(int rating)
        {
            var accountVm = App.GetService<AccountViewModel>();
            if (accountVm == null || !accountVm.IsLoggedIn)
            {
                ErrorMessage = "Please sign in to rate plugins.";
                return;
            }

            ErrorMessage = null;

            try
            {
                var response = await InfoPanelApiService.Instance.Client.Post_RatePluginAsync(
                    Slug,
                    new Body { Rating = rating });

                if (response != null)
                {
                    UserRating = response.Rating;
                    AverageRating = response.AverageRating;
                    RatingCount = response.RatingCount;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to rate plugin {Slug}", Slug);
                ErrorMessage = "Failed to submit rating.";
            }
        }
    }

    public class ReleaseViewModel
    {
        public string Version { get; }
        public string PublishedAt { get; }
        public ObservableCollection<string> ChangelogItems { get; } = [];

        public ReleaseViewModel(ApiClient.Releases release)
        {
            Version = release.Version;
            PublishedAt = release.PublishedAt;

            if (release.ChangelogItems != null)
            {
                foreach (var item in release.ChangelogItems)
                {
                    ChangelogItems.Add(item);
                }
            }
        }
    }
}
