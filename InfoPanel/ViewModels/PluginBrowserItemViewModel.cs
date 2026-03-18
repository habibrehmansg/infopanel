using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ApiClient;
using System;
using System.Collections.Generic;

namespace InfoPanel.ViewModels
{
    public partial class PluginBrowserItemViewModel : ObservableObject
    {
        public string Slug { get; }
        public string RepoName { get; }
        public string Name { get; }
        public string Description { get; }
        public string Icon { get; }
        public string Author { get; }
        public string AuthorAvatar { get; }
        public string Category { get; }
        public string? LatestVersion { get; }
        public double Downloads { get; }
        public double AverageRating { get; }
        public double RatingCount { get; }
        public string? DownloadUrl { get; }
        public string SourceUrl { get; }

        [ObservableProperty]
        private bool _isInstalled;

        [ObservableProperty]
        private bool _isUpdateAvailable;

        public string? InstalledVersion { get; }

        public PluginBrowserItemViewModel(Data data, Dictionary<string, string?> installedPlugins)
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
            DownloadUrl = data.DownloadUrl;
            SourceUrl = data.SourceUrl;

            string? installedVersion = null;
            if (installedPlugins.TryGetValue(Slug, out installedVersion)
                || installedPlugins.TryGetValue(RepoName, out installedVersion))
            {
                IsInstalled = true;
                InstalledVersion = installedVersion;

                if (LatestVersion != null && installedVersion != null
                    && Version.TryParse(LatestVersion, out var latest)
                    && Version.TryParse(installedVersion, out var installed)
                    && latest > installed)
                {
                    IsUpdateAvailable = true;
                }
            }
        }
    }
}
