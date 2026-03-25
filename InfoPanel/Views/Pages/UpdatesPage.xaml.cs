using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.ViewModels;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Text.RegularExpressions;
using System.Windows.Documents;

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
                var (summary, details) = ParseSummary(v.Changelog);

                ViewModel.UpdateVersions.Add(new UpdateVersion
                {
                    Version = v.Version,
                    Title = v.Changelog,
                    Expanded = first,
                    Changelog = details,
                    Summary = summary
                });

                first = false;
            }
        }

        private static (string? summary, string details) ParseSummary(string changelog)
        {
            const string summaryHeader = "## Summary";

            // Normalize line endings to \n for reliable parsing
            var normalized = changelog.Replace("\r\n", "\n");

            var summaryIndex = normalized.IndexOf(summaryHeader, StringComparison.Ordinal);
            if (summaryIndex < 0)
                return (null, changelog);

            var afterHeader = normalized[(summaryIndex + summaryHeader.Length)..];

            // Look for a line that is just "---"
            var separatorIndex = afterHeader.IndexOf("\n---\n", StringComparison.Ordinal);
            if (separatorIndex < 0)
                return (null, changelog);

            var summary = afterHeader[..separatorIndex].Trim();
            var details = afterHeader[(separatorIndex + 5)..].Trim(); // 5 = "\n---\n".Length

            if (string.IsNullOrEmpty(summary))
                return (null, changelog);

            return (summary, details);
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

        private void ChangelogTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is UpdateVersion version)
            {
                PopulateChangelogInlines(tb, version.Changelog);
            }
        }

        private void SummaryTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is UpdateVersion version && version.Summary != null)
            {
                PopulateChangelogInlines(tb, version.Summary);
            }
        }

        private void DetailsTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is UpdateVersion version)
            {
                PopulateChangelogInlines(tb, version.Changelog);
            }
        }

        private static void PopulateChangelogInlines(TextBlock textBlock, string markdown)
        {
            textBlock.Inlines.Clear();
            if (string.IsNullOrWhiteSpace(markdown)) return;

            var lines = markdown.Split('\n');
            bool firstLine = true;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("# ") && !trimmed.StartsWith("## "))
                {
                    // Top-level header (e.g. "# InfoPanel v1.3.1 Release Notes") — skip
                    continue;
                }

                if (!firstLine)
                    textBlock.Inlines.Add(new LineBreak());

                if (trimmed.StartsWith("## "))
                {
                    // Category header — bold, slightly larger
                    if (!firstLine)
                        textBlock.Inlines.Add(new LineBreak()); // extra spacing before header

                    var header = trimmed[3..];
                    textBlock.Inlines.Add(new Run(header) { FontWeight = FontWeights.SemiBold, FontSize = 13 });
                }
                else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    // Bullet item
                    var text = trimmed[2..];
                    AddInlineText(textBlock, $"  \u2022 {text}");
                }
                else
                {
                    AddInlineText(textBlock, trimmed);
                }

                firstLine = false;
            }
        }

        private static readonly Regex BoldPattern = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

        private static void AddInlineText(TextBlock textBlock, string text)
        {
            var matches = BoldPattern.Matches(text);
            if (matches.Count == 0)
            {
                textBlock.Inlines.Add(new Run(text));
                return;
            }

            int pos = 0;
            foreach (Match match in matches)
            {
                if (match.Index > pos)
                    textBlock.Inlines.Add(new Run(text[pos..match.Index]));

                textBlock.Inlines.Add(new Run(match.Groups[1].Value) { FontWeight = FontWeights.SemiBold });
                pos = match.Index + match.Length;
            }

            if (pos < text.Length)
                textBlock.Inlines.Add(new Run(text[pos..]));
        }

        private async void ButtonUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.VersionModel?.DownloadUrl is string url)
            {
                ViewModel.DownloadInProgress = true;
                ViewModel.DownloadProgress = 0;

                var cts = new CancellationTokenSource();
                IProgress<DownloadProgressArgs> progressReporter = new Progress<DownloadProgressArgs>(args =>
                {
                    ViewModel.DownloadProgress = args.PercentComplete;
                    ViewModel.DownloadStatus = $"{FormatBytes(args.BytesReceived)} / {FormatBytes(args.TotalBytes)} ({args.PercentComplete:F0}%)";
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

        private static string FormatBytes(double bytes)
        {
            return bytes switch
            {
                >= 1_073_741_824 => $"{bytes / 1_073_741_824:F2} GB",
                >= 1_048_576 => $"{bytes / 1_048_576:F1} MB",
                >= 1_024 => $"{bytes / 1_024:F0} KB",
                _ => $"{bytes:F0} B"
            };
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
