using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using InfoPanel.Models;
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

    private UpdateChecker() { }

    public async Task CheckAsync()
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

                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
                UpdateAvailable = Version.Parse(latest.Version) > Version.Parse(currentVersion);

                if (UpdateAvailable)
                {
                    Logger.Information("Update available: {Version}", latest.Version);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates");
        }

        UpdateCheckCompleted?.Invoke();
    }

    public class VersionEntry
    {
        public string Version { get; set; } = string.Empty;
        public string Changelog { get; set; } = string.Empty;
        public ICollection<string> ChangelogItems { get; set; } = [];
        public string? DownloadUrl { get; set; }
    }
}
