using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;

namespace InfoPanel.Services;

public sealed class UpdateChecker
{
    private static readonly Lazy<UpdateChecker> _instance = new(() => new UpdateChecker());
    private static readonly ILogger Logger = Log.ForContext<UpdateChecker>();

    public static UpdateChecker Instance => _instance.Value;

    public VersionModel? LatestVersion { get; private set; }
    public IReadOnlyList<VersionEntry>? Versions { get; private set; }
    public bool UpdateAvailable { get; private set; }

    public event Action? UpdateCheckCompleted;

    private UpdateChecker()
    {
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    private static void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (args.Contains("action") && args["action"] == "viewUpdate")
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (System.Windows.Application.Current.MainWindow is Views.Windows.MainWindow mainWindow)
                {
                    mainWindow.RestoreWindow();
                    mainWindow.Navigate(typeof(Views.Pages.UpdatesPage));
                }
            });
        }
    }

    public async Task CheckAsync(bool showNotification = false)
    {
        try
        {
            var response = await InfoPanelApiService.Instance.Client.Get_ListVersionsAsync();

            var versions = new List<VersionEntry>();
            foreach (var v in response.Versions)
            {
                versions.Add(new VersionEntry
                {
                    Version = v.Version.StartsWith("v") ? v.Version : $"v{v.Version}",
                    Changelog = v.Changelog,
                    ChangelogItems = v.ChangelogItems,
                    DownloadUrl = v.DownloadUrl
                });
            }

            Versions = versions;

            var latest = response.Versions.FirstOrDefault();
            if (latest != null)
            {
                LatestVersion = new VersionModel
                {
                    Version = latest.Version,
                    DownloadUrl = latest.DownloadUrl,
                    Changelog = latest.Changelog,
                    ChangelogItems = latest.ChangelogItems
                };

                var currentVersion = VersionHelper.AppVersion;
                UpdateAvailable = IsNewerVersion(latest.Version, currentVersion);

                if (UpdateAvailable)
                {
                    Logger.Information("Update available: {Version}", latest.Version);
                    if (showNotification)
                    {
                        ShowUpdateNotification(latest.Version);
                    }
                }
                else
                {
                    ClearUpdateNotification();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates");
        }

        UpdateCheckCompleted?.Invoke();
    }

    private static void ShowUpdateNotification(string version)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("InfoPanel Update Available")
                .AddText($"Version {version} is ready to download.")
                .AddButton(new ToastButton()
                    .SetContent("View Update")
                    .AddArgument("action", "viewUpdate"))
                .Show(toast =>
                {
                    toast.Tag = "update";
                    toast.Group = "infopanel";
                });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to show update notification");
        }
    }

    private static void ClearUpdateNotification()
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove("update", "infopanel");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clear update notification");
        }
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        var (latestBase, latestPre) = SplitVersion(latest);
        var (currentBase, currentPre) = SplitVersion(current);

        var cmp = Version.Parse(latestBase).CompareTo(Version.Parse(currentBase));
        if (cmp != 0) return cmp > 0;

        // Same base version: stable > prerelease
        if (currentPre != null && latestPre == null) return true;
        if (currentPre == null && latestPre != null) return false;
        return false;
    }

    private static (string baseVersion, string? prerelease) SplitVersion(string version)
    {
        version = version.TrimStart('v');
        var idx = version.IndexOf('-');
        return idx >= 0 ? (version[..idx], version[(idx + 1)..]) : (version, null);
    }

    public class VersionEntry
    {
        public string Version { get; set; } = string.Empty;
        public string Changelog { get; set; } = string.Empty;
        public ICollection<string> ChangelogItems { get; set; } = [];
        public string? DownloadUrl { get; set; }
    }
}
