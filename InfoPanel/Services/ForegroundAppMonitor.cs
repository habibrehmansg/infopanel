using InfoPanel.Models;
using InfoPanel.Utils;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace InfoPanel.Services
{
    /// <summary>Polls the foreground window and shows/hides profiles based on program-specific trigger rules.</summary>
    public sealed class ForegroundAppMonitor : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<ForegroundAppMonitor>();
        private static readonly Lazy<ForegroundAppMonitor> _instance = new(() => new ForegroundAppMonitor());
        public static ForegroundAppMonitor Instance => _instance.Value;

        private const int PollIntervalMs = 800;

        private ForegroundAppMonitor() { }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            // Poll immediately so if the trigger app is already in foreground when we start, we show its panel right away.
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = ConfigModel.Instance.Settings;
                    var profiles = ConfigModel.Instance.GetProfilesCopy();

                    if (!settings.ProgramSpecificPanelsEnabled)
                    {
                        ApplyVisibility(profiles, matchingTriggerProfiles: null, hideOthers: false, token);
                        await Task.Delay(PollIntervalMs, token);
                        continue;
                    }

                    string? foregroundName = ForegroundWindowHelper.GetForegroundProcessName();
                    var matchingTriggerProfiles = GetMatchingTriggerProfiles(profiles, foregroundName);
                    bool hideOthers = settings.HideOtherProfilesWhenProgramSpecificShown;

                    ApplyVisibility(profiles, matchingTriggerProfiles, hideOthers, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "ForegroundAppMonitor poll error");
                }

                await Task.Delay(PollIntervalMs, token);
            }
        }

        private static List<Profile> GetMatchingTriggerProfiles(List<Profile> profiles, string? foregroundName)
        {
            if (string.IsNullOrWhiteSpace(foregroundName))
                return new List<Profile>();

            var foreground = foregroundName.Trim();
            var normalizedForeground = NormalizeProcessName(foreground);
            var list = new List<Profile>();

            foreach (var profile in profiles)
            {
                if (!profile.Active || string.IsNullOrWhiteSpace(profile.TriggerProcessNames))
                    continue;

                var names = profile.TriggerProcessNames
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var name in names)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (string.Equals(NormalizeProcessName(name), normalizedForeground, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(profile);
                        break;
                    }
                }
            }

            return list;
        }

        private static string NormalizeProcessName(string name)
        {
            var s = name.Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return s[..^4];
            return s;
        }

        private static void ApplyVisibility(
            List<Profile> profiles,
            List<Profile>? matchingTriggerProfiles,
            bool hideOthers,
            CancellationToken token)
        {
            bool hasMatchingTrigger = matchingTriggerProfiles is { Count: > 0 };

            foreach (var profile in profiles)
            {
                if (token.IsCancellationRequested) return;

                bool shouldBeVisible;
                if (hasMatchingTrigger && hideOthers)
                {
                    shouldBeVisible = profile.Active && matchingTriggerProfiles!.Contains(profile);
                }
                else if (hasMatchingTrigger && !hideOthers)
                {
                    bool isMatchingTrigger = matchingTriggerProfiles!.Contains(profile);
                    bool isAlwaysOn = string.IsNullOrWhiteSpace(profile.TriggerProcessNames);
                    shouldBeVisible = profile.Active && (isMatchingTrigger || isAlwaysOn);
                }
                else
                {
                    // No matching trigger: show only always-on (no trigger) Active profiles
                    shouldBeVisible = profile.Active && string.IsNullOrWhiteSpace(profile.TriggerProcessNames);
                }

                bool isOpen = DisplayWindowManager.Instance.IsWindowOpen(profile.Guid);
                if (shouldBeVisible == isOpen)
                    continue;

                var app = Application.Current as App;
                if (app == null) continue;

                try
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (shouldBeVisible)
                            app.ShowDisplayWindow(profile);
                        else
                            app.CloseDisplayWindow(profile);
                    });
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "ForegroundAppMonitor failed to update window for profile {ProfileName}", profile.Name);
                }
            }
        }

        /// <summary>When program-specific panels are turned off, show all Active profiles and close the rest. Call from UI thread or dispatcher.</summary>
        public static void ReconcileVisibilityToActiveOnly()
        {
            var app = Application.Current as App;
            if (app == null) return;

            var profiles = ConfigModel.Instance.GetProfilesCopy();
            foreach (var profile in profiles)
            {
                bool shouldBeVisible = profile.Active;
                bool isOpen = DisplayWindowManager.Instance.IsWindowOpen(profile.Guid);
                if (shouldBeVisible == isOpen) continue;
                try
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (shouldBeVisible)
                            app.ShowDisplayWindow(profile);
                        else
                            app.CloseDisplayWindow(profile);
                    });
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "ReconcileVisibilityToActiveOnly failed for profile {ProfileName}", profile.Name);
                }
            }
        }
    }
}
