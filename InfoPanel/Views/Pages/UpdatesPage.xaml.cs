using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.ViewModels;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for AboutPage.xaml
    /// </summary>
    public partial class UpdatesPage : Page
    {
        private static readonly ILogger Logger = Log.ForContext<UpdatesPage>();

        public UpdatesViewModel ViewModel
        {
            get;
        }

        public UpdatesPage(UpdatesViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
            ApplyUpdateCheckResults();
        }

        private void ButtonCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdates();
        }

        private void ApplyUpdateCheckResults()
        {
            var checker = UpdateChecker.Instance;

            if (checker.LatestVersion != null)
            {
                ApplyVersionResult(checker);
                ApplyChangelog(checker);
            }
            else
            {
                // Startup check hasn't completed yet — listen for it
                ViewModel.UpdateCheckInProgress = true;
                checker.UpdateCheckCompleted += OnStartupCheckCompleted;
            }
        }

        private void OnStartupCheckCompleted()
        {
            UpdateChecker.Instance.UpdateCheckCompleted -= OnStartupCheckCompleted;
            Dispatcher.Invoke(() =>
            {
                var checker = UpdateChecker.Instance;
                ApplyVersionResult(checker);
                ApplyChangelog(checker);
                ViewModel.UpdateCheckInProgress = false;
            });
        }

        private void ApplyVersionResult(UpdateChecker checker)
        {
            if (checker.UpdateAvailable && checker.LatestVersion != null)
            {
                ViewModel.VersionModel = checker.LatestVersion;
                ViewModel.UpdateAvailable = true;
            }
            else
            {
                ViewModel.UpdateAvailable = false;
            }
        }

        private void ApplyChangelog(UpdateChecker checker)
        {
            ViewModel.UpdateVersions.Clear();

            if (checker.Versions == null) return;

            bool first = true;
            foreach (var v in checker.Versions)
            {
                ViewModel.UpdateVersions.Add(new UpdateVersion
                {
                    Version = v.Version,
                    Title = v.Changelog,
                    Expanded = first,
                    ChangelogItems = new ObservableCollection<string>(v.ChangelogItems)
                });

                first = false;
            }
        }

        private async void CheckUpdates()
        {
            ViewModel.UpdateCheckInProgress = true;

            var checker = UpdateChecker.Instance;
            await checker.CheckAsync();

            ApplyVersionResult(checker);
            ApplyChangelog(checker);

            ViewModel.UpdateCheckInProgress = false;
        }

        private async void ButtonUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.VersionModel?.DownloadUrl is string url)
            {
                ViewModel.DownloadInProgress = true;
                ViewModel.DownloadProgress = 0;

                var cts = new CancellationTokenSource();
                IProgress<DownloadProgressArgs> progressReporter = new Progress<DownloadProgressArgs>(progressReporter =>
                {
                    ViewModel.DownloadProgress = progressReporter.PercentComplete;
                });

                using (var stream = await DownloadStreamWithProgressAsync(url, cts.Token, progressReporter))
                {
                    try
                    {
                        var downloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "updates");
                        if (!Directory.Exists(downloadPath))
                        {
                            Directory.CreateDirectory(downloadPath);
                        }

                        var filePath = Path.Combine(downloadPath, "InfoPanelSetup.exe");

                        SaveStreamToFile(stream, filePath);

                        Process.Start(filePath);
                        Environment.Exit(0);
                    }
                    catch { }
                }

                ViewModel.DownloadInProgress = false;
            }
        }

        public static void SaveStreamToFile(Stream stream, string filePath)
        {
            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                stream.CopyTo(fileStream);
            }
        }

        public static async Task<Stream> DownloadStreamWithProgressAsync(string url, CancellationToken cancellationToken, IProgress<DownloadProgressArgs> progessReporter)
        {
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var receivedBytes = 0;
            var buffer = new byte[4096];
            var totalBytes = Convert.ToDouble(response.Content.Headers.ContentLength);

            var memStream = new MemoryStream();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesRead = await stream.ReadAsync(buffer, cancellationToken);

                await memStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                if (bytesRead == 0)
                {
                    break;
                }
                receivedBytes += bytesRead;

                var args = new DownloadProgressArgs(receivedBytes, totalBytes);
                progessReporter.Report(args);
            }

            memStream.Position = 0;
            return memStream;
        }

        public class DownloadProgressArgs : EventArgs
        {
            public DownloadProgressArgs(int bytesReceived, double totalBytes)
            {
                BytesReceived = bytesReceived;
                TotalBytes = totalBytes;
            }

            public double TotalBytes { get; }

            public double BytesReceived { get; }

            public double PercentComplete => 100 * (BytesReceived / TotalBytes);
        }
    }
}
