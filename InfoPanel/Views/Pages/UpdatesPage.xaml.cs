using Flurl;
using Flurl.Http;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using System;
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
        public UpdatesViewModel ViewModel
        {
            get;
        }

        public UpdatesPage(UpdatesViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = viewModel;

            InitializeComponent();
            CheckUpdates();
        }

        private void ButtonCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdates();
        }

        private async void CheckUpdates()
        {
            ViewModel.UpdateCheckInProgress = true;

            var latestVersion = await "https://update.infopanel.net"
                .AppendPathSegment("latest")
                .GetAsync()
                .ReceiveJson<VersionModel>();

            await Task.Delay(500);

            if (IsNewerVersionAvailable(ViewModel.Version, latestVersion.Version))
            {
                ViewModel.VersionModel = latestVersion;
                ViewModel.UpdateAvailable = true;
            } else
            {
                ViewModel.UpdateAvailable = false;
            }

            ViewModel.UpdateCheckInProgress = false;
        }

        private bool IsNewerVersionAvailable(string currentVersion, string newVersion)
        {
            Version current = Version.Parse(currentVersion);
            Version latest = Version.Parse(newVersion);

            return latest > current;
        }

        private async void ButtonUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.VersionModel?.Url is string url)
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
            try
            {
                using IFlurlResponse response = await url.GetAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                using var stream = await response.GetStreamAsync();
                var receivedBytes = 0;
                var buffer = new byte[4096];
                var totalBytes = Convert.ToDouble(response.ResponseMessage.Content.Headers.ContentLength);

                var memStream = new MemoryStream();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                    await memStream.WriteAsync(buffer, 0, bytesRead);

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
            catch (Exception e)
            {
                throw e;
            }
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
