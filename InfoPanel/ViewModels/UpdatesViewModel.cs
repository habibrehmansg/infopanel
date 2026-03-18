using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System.Collections.ObjectModel;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public class UpdatesViewModel : ObservableObject
    {
        public string Version { get; set; }

        public VersionModel? VersionModel { get; set; }

        private bool _updateCheckInProgress = false;

        public bool UpdateCheckInProgress
        {
            get { return _updateCheckInProgress; }
            set { SetProperty(ref _updateCheckInProgress, value); }
        }

        private bool _downloadInProgress = false;
        public bool DownloadInProgress
        {
            get { return _downloadInProgress; }
            set { SetProperty(ref _downloadInProgress, value); }
        }

        private double _downloadProgress = 0;

        public double DownloadProgress
        {
            get { return _downloadProgress; }
            set { SetProperty(ref _downloadProgress, value); }
        }

        private string _downloadStatus = string.Empty;
        public string DownloadStatus
        {
            get { return _downloadStatus; }
            set { SetProperty(ref _downloadStatus, value); }
        }

        private bool _updateAvailable = false;
        public bool UpdateAvailable
        {
            get { return _updateAvailable; }
            set { SetProperty(ref _updateAvailable, value); }
        }

        public ObservableCollection<UpdateVersion> UpdateVersions { get; } = [];

        public UpdatesViewModel()
        {
            Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
        }
    }

    public class UpdateVersion()
    {
        public required string Version { get; set; }
        public required string Title { get; set; }
        public bool Expanded { get; set; } = false;
        public required string Changelog { get; set; }
    }

}
