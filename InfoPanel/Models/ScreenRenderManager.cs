using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Models
{

    // Manages version tracking for display items to detect updates and avoid unnecessary redraws.
    public static class ScreenRenderManager
    {
        private static readonly ILogger Logger = Log.ForContext("SourceContext", nameof(ScreenRenderManager));

        // Internal class for managing the maximum target FPS and version for a profile.
        private class ProfileInfo
        {
            // Manage version information associated with the profile.
            private long _version;
            // Time when the profile version was last updated.
            private long _lastUpdateTimeFrequency = GetCurrentSecondStartTimestamp();
            // Profile version update interval. Default to 1 FPS.
            private long _updateIntervalFrequency = Stopwatch.Frequency;
            // Lock object protecting reads and writes to the update interval (_updateIntervalFrequency).
            private readonly object _fpsLock = new();

            // Returns the current monotonic timestamp aligned to the start of the current UTC second.
            private static long GetCurrentSecondStartTimestamp()
            {
                var currentTimeFrequency = Stopwatch.GetTimestamp();
                var subsecondTicks = DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond;
                var elapsedFrequency = (long)(subsecondTicks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
                return currentTimeFrequency - elapsedFrequency;
            }

            // Update version information associated with the profile.
            public void IncVersion()
            {
                Interlocked.Increment(ref _version);
                // Logger.Debug("Increment Version");
            }
            // Get version information associated with the profile.
            public long GetVersion()
            {
                return Interlocked.Read(ref _version);
            }
            // Update the version when needed based on the update interval (maximum FPS).
            // Return the time remaining until the next version update in Stopwatch ticks, whether or not an update occurred. Use long values instead of DateTime to reduce CPU load.
            public long UpdateVersionIfNeeded()
            {
                var currentTimeFrequency = Stopwatch.GetTimestamp();
                var interval = Volatile.Read(ref _updateIntervalFrequency);
                var elapsedTime = currentTimeFrequency - _lastUpdateTimeFrequency;
                if (elapsedTime >= interval)
                {
                    IncVersion();
                    // Keep the original cadence so one-second updates stay aligned to second boundaries.
                    var elapsedWithinInterval = elapsedTime % interval;
                    _lastUpdateTimeFrequency = currentTimeFrequency - elapsedWithinInterval;
                    return interval - elapsedWithinInterval;
                }
                else
                {
                    return interval == long.MaxValue ? long.MaxValue : _lastUpdateTimeFrequency - currentTimeFrequency + interval;
                }
            }
            // Calculate and set the update interval in Stopwatch ticks from the specified target FPS.
            public void UpdateMaxFps(int targetFps)
            {
                lock (_fpsLock)
                {
                    var targetFrameTimeFrequency = targetFps == 0 ? long.MaxValue : Stopwatch.Frequency / targetFps;
                    if (targetFps == 1)
                    {
                        _lastUpdateTimeFrequency = GetCurrentSecondStartTimestamp();
                    }
                    Volatile.Write(ref _updateIntervalFrequency, targetFrameTimeFrequency);
                }
            }
        }

        // HWiNFO sensor reading interval (ms).
        public static int HWPoolIntervalMilliseconds { get; } = 500;
        // Libre Hardware Monitor sensor reading interval (ms).
        public static int LibrePoolIntervalMilliseconds { get; } = 2000;
        // Minimum and maximum target FPS for InfoPanel.
        // 0 FPS is used when displaying static items.
        // No panels are defined with a rate above 60 FPS, so use 60 FPS as the maximum. This may need to change if more panel types are added.
        public const int MinFps = 0;
        public const int MaxFps = 60;
        // Periodically refresh profiles at least once per second as a fallback for missed update requirements.
        private const int MinimumProfileRefreshFps = 1;
        // Minimum and maximum graph update intervals.
        public const int MinTargetGraphUpdateRate = 100;
        public const int MaxTargetGraphUpdateRate = 3000;
        // Vendor names used for reflection because there is no base class for retrieving panel information. Update this list when adding a panel vendor.
        private static string[] PanelVendors { get; } = ["Beada", "Jl", "Jonsbo", "LianLi", "Thermalright", "Thermaltake", "Turing", "Vmax"];

        // List of profiles.
        private static ObservableCollection<Profile> Profiles { get; } = ConfigModel.Instance.Profiles;
        // Application settings.
        private static Settings Settings { get; } = ConfigModel.Instance.Settings;
        // Profiles associated with enabled panels.
        // Disabled or unassociated profiles are not managed. This information is updated when panel settings change.
        private static ConcurrentDictionary<Guid, Profile> EnabledProfiles { get; set; } = new();
        // Dictionary of maximum FPS values for profiles associated with panels.
        private static ConcurrentDictionary<Guid, int> MaxTargetFrameRateProfileByPanel { get; set; } = new();
        // Dictionary of maximum FPS values for profiles associated with DisplayItems.
        private static ConcurrentDictionary<Guid, int> MaxTargetFrameRateProfileByDisplayItem { get; set; } = new();
        // Dictionary of managed DisplayItems.
        private static ConcurrentDictionary<Guid, DisplayItem> DisplayItems { get; set; } = new();
        // Dictionary mapping DisplayItems to SensorIDs.
        private static ConcurrentDictionary<Guid, string> DisplayItemIdToSensorId { get; set; } = new();
        // Dictionary of DisplayItems associated with each sensor ID.
        // Used to determine whether a sensor is used by multiple DisplayItems.
        private readonly static ConcurrentDictionary<string, ConcurrentDictionary<Guid, DisplayItem>> SensorIdToDisplayItems = new();

        // Dictionary of GUIDs and DisplayItems expected to update infrequently.
        private static ConcurrentDictionary<Guid, DisplayItem> LowUpdateFrequencyDisplayItemIds { get; set; } = new();
        // Dictionary of the last retrieved string values for infrequently updated DisplayItems.
        private static ConcurrentDictionary<Guid, string> LowUpdateFrequencyDisplayItemStringValues { get; set; } = new();
        // Dictionary of the last retrieved long values for infrequently updated DisplayItems.
        private static ConcurrentDictionary<Guid, long> LowUpdateFrequencyDisplayItemLongValues { get; set; } = new();
        // Time when the last value was retrieved for an infrequently updated DisplayItem.
        static long LastLowUpdateFrequencyCheckTime { get; set; } = Stopwatch.GetTimestamp();
        // Dictionary for managing profile versions and required FPS values.
        private static ConcurrentDictionary<Guid, ProfileInfo> ProfileInfos { get; set; } = new();
        // Flag indicating that panel or DisplayItem changes need to be detected at startup or after user actions.
        private static int _profilesDirty;

        // Update each profile version according to its target FPS (the main process of this class, called when InfoPanel starts).
        public static async Task UpdateProfilesVersions(CancellationToken cancellationToken)
        {
            SetProfilesOrSettingsDirty();
            var retryDelayMilliseconds = 50;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await UpdateProfileVersionsOnce(cancellationToken);
                    retryDelayMilliseconds = 50;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Logger.Error(ex, "An error occurred while updating profile versions. Retrying in {DelayMilliseconds} ms.", retryDelayMilliseconds);
                    SetProfilesOrSettingsDirty();
                    await Task.Delay(retryDelayMilliseconds, cancellationToken);
                    retryDelayMilliseconds = Math.Min(retryDelayMilliseconds * 2, 3000);
                }
            }
        }

        // Perform one profile update cycle. Exceptions are handled by UpdateProfilesVersions, which applies retry backoff.
        private static async Task UpdateProfileVersionsOnce(CancellationToken cancellationToken)
        {
            try
            {
                CheckProfilesOrSettingsDirty();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to rebuild profile and panel state.");
                throw;
            }
            // Store the wait duration until the next loop in Stopwatch ticks.
            long nextUpdateFrequency = long.MaxValue;
            var currentTimeFrequency = Stopwatch.GetTimestamp();

            // Evaluate infrequently updated DisplayItems and increment the associated profile version when a value changes (run every second).
            if (currentTimeFrequency - LastLowUpdateFrequencyCheckTime >= Stopwatch.Frequency)
            {
                LastLowUpdateFrequencyCheckTime = currentTimeFrequency;
                foreach (KeyValuePair<Guid, DisplayItem> kvp in LowUpdateFrequencyDisplayItemIds)
                {
                    switch (kvp.Value)
                    {
                        case TextDisplayItem text:
                            if (!LowUpdateFrequencyDisplayItemStringValues.TryGetValue(text.Guid, out var lastValue) || lastValue is null)
                            {
                                Logger.Warning("Cannot get the previous text value for low-frequency DisplayItem {ProfileGuid}/{DisplayItemGuid} ({Name}). Rebuilding profile state.", text.ProfileGuid, text.Guid, text.Name);
                                SetProfilesOrSettingsDirty();
                                break;
                            }

                            string currentValue;
                            try
                            {
                                currentValue = text.EvaluateText();
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning(ex, "Failed to evaluate low-frequency text DisplayItem {ProfileGuid}/{DisplayItemGuid} ({Name}).", text.ProfileGuid, text.Guid, text.Name);
                                throw;
                            }

                            if (lastValue != currentValue)
                            {
                                // Update the version because a change in the target DisplayItem's value was detected.
                                if (ProfileInfos.TryGetValue(text.ProfileGuid, out var profileInfo))
                                {
                                    profileInfo.IncVersion();
                                    LowUpdateFrequencyDisplayItemStringValues[text.Guid] = currentValue;
                                    Logger.Debug("Increment Profile version. {profileId} : {currentValue} : {version}", text.ProfileGuid, currentValue, profileInfo.GetVersion());
                                }
                                else
                                {
                                    // This should not normally happen, so rebuild the internal state.
                                    SetProfilesOrSettingsDirty();
                                }
                            }
                            break;
                        case ImageDisplayItem image:
                            // For URLs, initialize with the current time so requests can be made at the configured interval.
                            // Use GetOrAdd to preserve the original interval baseline.
                            if (image.Type == ImageDisplayItem.ImageType.URL)
                            {
                                _ = LowUpdateFrequencyDisplayItemLongValues.GetOrAdd(image.Guid, currentTimeFrequency);
                            }

                            _ = LowUpdateFrequencyDisplayItemLongValues.TryGetValue(image.Guid, out var lastUpdateTimeFrequency);

                            if ((currentTimeFrequency - lastUpdateTimeFrequency) / Stopwatch.Frequency >= image.RefreshInterval)
                            {
                                // This is the HTTP image update interval, so update the ProfileInfo version to trigger an HTTP request.
                                if (ProfileInfos.TryGetValue(image.ProfileGuid, out var profileInfo))
                                {
                                    profileInfo.IncVersion();
                                    LowUpdateFrequencyDisplayItemLongValues[image.Guid] = currentTimeFrequency;
                                    Logger.Debug("Increment Profile version. {profileId} : {currentValue} : {version}", image.ProfileGuid, image.HttpUrl, profileInfo.GetVersion());
                                }
                                else
                                {
                                    // This should not normally happen, so rebuild the internal state.
                                    Logger.Warning("Cannot get profileInfo for low-frequency image DisplayItem {ProfileGuid}/{DisplayItemGuid}. Rebuilding profile state.", image.ProfileGuid, image.Guid);
                                    SetProfilesOrSettingsDirty();
                                }
                            }
                            break;
                    }
                }
            }

            // Check whether each profile version needs updating and find the shortest wait until the next update.
            foreach (var profileId in ProfileInfos.Keys)
            {
                if (ProfileInfos.TryGetValue(profileId, out var profileInfo))
                {
                    try
                    {
                        nextUpdateFrequency = Math.Min(nextUpdateFrequency, profileInfo.UpdateVersionIfNeeded());
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to update the version for profile {ProfileGuid}.", profileId);
                        throw;
                    }
                }
                else
                {
                    // This should not normally happen, so rebuild the internal state.
                    Logger.Warning("Cannot get profileInfo for profile {ProfileGuid}. Rebuilding profile state.", profileId);
                    SetProfilesOrSettingsDirty();
                }
            }
            if (IsProfilesOrSettingsDirty())
            {
                // Rebuild inconsistent internal state promptly. Rebuilding always increments the profile version.
                await Task.Delay(10, cancellationToken);
            }
            else
            {
                // Calculate the shortest wait until the next update and wait.
                // Wait at least 10 ms and at most 1000 ms. This interval is used to check for updates to infrequently updated objects.
                await Task.Delay((int)Math.Clamp(Math.Ceiling(nextUpdateFrequency * 1000.0 / Stopwatch.Frequency), 10.0, 1000.0), cancellationToken);
            }
        }

        // Return the target profile version, or 0 if the profile does not exist.
        public static long GetProfileVersion(Guid profileId)
        {
            if (ProfileInfos.TryGetValue(profileId, out var profileInfo))
            {
                var version = profileInfo.GetVersion();
                // Logger.Debug("GetProfileVersion : {profileId}:{version}", profileId, version);
                return version;
            }
            return 0;
        }

        // Update the target profile version.
        public static void IncProfileVersion(Guid profileId)
        {
            // Create the profile if it does not exist.
            var profileInfo = ProfileInfos.GetOrAdd(profileId, _ => new ProfileInfo());
            profileInfo.IncVersion();
            Logger.Debug("IncProfileVersion : {profileId}:{version}", profileId, profileInfo.GetVersion());
        }

        // On a sensor ID update notification, update the versions of profiles associated with that sensor ID.
        public static void IncProfileVersionByItem(string itemKey)
        {
            if (string.IsNullOrEmpty(itemKey)) return;
            if (!SensorIdToDisplayItems.TryGetValue(itemKey, out var displayItems)) return;

            Logger.Debug("IncProfileVersionByItem : {itemKey}", itemKey);

            foreach (var displayItem in displayItems.Values)
            {
                IncProfileVersion(displayItem.ProfileGuid);
            }
        }

        // Mark profile and panel settings data for rebuilding after startup or a UI change.
        public static void SetProfilesOrSettingsDirty()
        {
            Interlocked.Exchange(ref _profilesDirty, 1);
        }

        // Rebuild the internal data once when a rebuild has been requested.
        private static void CheckProfilesOrSettingsDirty()
        {
            if (Interlocked.Exchange(ref _profilesDirty, 0) == 1)
            {
                CheckUpdate();
            }
        }
        // Check whether profile and panel settings data needs rebuilding.
        public static bool IsProfilesOrSettingsDirty()
        {
            return Volatile.Read(ref _profilesDirty) == 1;
        }

        // Rebuild internal profile data from the current panel and DisplayItem settings. 
        // Remove data for items and profiles that are no longer in use, then update each profile's target FPS and version.
        private static void CheckUpdate()
        {
            // Update panel-related information.
            CheckUsedProfilesAndMaxFpsByPanel();

            // Copy the list of DisplayItems.
            var lastDisplayItems = new ConcurrentDictionary<Guid, DisplayItem>(DisplayItems);
            // Copy profile-related information to identify profiles that are not managed.
            var lastEnabledProfiles = new ConcurrentDictionary<Guid, Profile>(EnabledProfiles);
            // Update profile-related information.
            foreach (var profile in EnabledProfiles.Values)
            {
                CheckProfileInfoAndMaxFpsByProfile(profile, lastDisplayItems, lastEnabledProfiles);
            }
            // Remove DisplayItems that are no longer in use.
            foreach (var displayItem in lastDisplayItems.Values)
            {
                RemoveDisplayItem(displayItem);
            }
            // Remove profiles that are no longer in use.
            foreach (var profileId in lastEnabledProfiles.Keys)
            {
                _ = ProfileInfos.TryRemove(profileId, out _);
                _ = EnabledProfiles.TryRemove(profileId, out _);
            }

            // Use the lower of the maximum FPS from the DisplayItem and the maximum FPS from the panel as the target FPS, then update each profile version.
            foreach (var enableProfileId in EnabledProfiles.Keys)
            {
                var profileInfo = ProfileInfos.GetOrAdd(enableProfileId, _ => new ProfileInfo());
                var targetFps = Math.Min(MaxTargetFrameRateProfileByDisplayItem[enableProfileId], MaxTargetFrameRateProfileByPanel[enableProfileId]);
                profileInfo.UpdateMaxFps(Math.Max(MinimumProfileRefreshFps, targetFps));
                profileInfo.IncVersion();
                Logger.Debug("Increment Profile version. {ProfileId} : {targtFps} FPS : {version}", enableProfileId, Math.Min(MaxTargetFrameRateProfileByDisplayItem[enableProfileId], MaxTargetFrameRateProfileByPanel[enableProfileId]), profileInfo.GetVersion());
            }
        }

        // Register the DisplayItem GUID in SensorIdToDisplayItems (sensor ID to DisplayItem mapping).
        public static void AddDisplayItem(DisplayItem displayItem)
        {
            _ = DisplayItems.GetOrAdd(displayItem.Guid, displayItem);
            string sensorId = GetSensorIdFromDisplayItem(displayItem);

            if (sensorId == string.Empty) return;
            var displayItems = SensorIdToDisplayItems.GetOrAdd(sensorId, _ => new ConcurrentDictionary<Guid, DisplayItem>());
            displayItems.GetOrAdd(displayItem.Guid, displayItem);
            Logger.Debug("AddDisplayItem : {sensorId} : {displayItem}", sensorId, displayItem.Guid);

            DisplayItemIdToSensorId.GetOrAdd(displayItem.Guid, sensorId);
            Logger.Debug("AddDisplayItem : {displayItem} : {sensorId}", displayItem.Guid, sensorId);
        }


        // Remove the DisplayItem GUID from SensorIdToDisplayItems (sensor ID to DisplayItem mapping).
        public static void RemoveDisplayItem(DisplayItem displayItem)
        {
            string sensorId = GetSensorIdFromDisplayItem(displayItem);
            if (sensorId != string.Empty)
            {
                if (SensorIdToDisplayItems.TryGetValue(sensorId, out var displayItems))
                {
                    _ = displayItems.TryRemove(displayItem.Guid, out _);
                    Logger.Debug("RemoveDisplayItem : {sensorId} : {displayItem}", sensorId, displayItem.Guid);
                }
            }
            DisplayItemIdToSensorId.TryRemove(displayItem.Guid, out _);
            Logger.Debug("RemoveDisplayItem : {displayItem} : {sensorId}", displayItem.Guid, sensorId);

            _ = LowUpdateFrequencyDisplayItemIds.TryRemove(displayItem.Guid, out _);
            _ = LowUpdateFrequencyDisplayItemStringValues.TryRemove(displayItem.Guid, out _);
            _ = LowUpdateFrequencyDisplayItemLongValues.TryRemove(displayItem.Guid, out _);
            _ = DisplayItems.TryRemove(displayItem.Guid, out _);
        }

        // Update internal state using the latest list of DisplayItems.
        private static void CheckProfileInfoAndMaxFpsByProfile(Profile profile, ConcurrentDictionary<Guid, DisplayItem> lastDisplayItems, ConcurrentDictionary<Guid, Profile> lastEnabledProfiles)
        {
            int tempMaxFps = 0;
            _ = lastEnabledProfiles.TryRemove(profile.Guid, out _);
            var displayItems = SharedModel.Instance.GetProfileDisplayItemsCopy(profile);
            foreach (var displayItem in displayItems)
            {
                if (displayItem.Hidden) continue;
                if (displayItem is GroupDisplayItem group)
                {
                    foreach (var childDisplayItem in group.DisplayItemsCopy)
                    {
                        if (childDisplayItem.Hidden) continue;
                        var fps = GetFpsByDisplayItem(childDisplayItem);
                        if (fps > tempMaxFps)
                        {
                            tempMaxFps = fps;
                        }
                        AddDisplayItem(childDisplayItem);
                        _ = lastDisplayItems.TryRemove(childDisplayItem.Guid, out _);
                    }
                }
                else
                {
                    var fps = GetFpsByDisplayItem(displayItem);
                    if (fps > tempMaxFps)
                    {
                        tempMaxFps = fps;
                    }
                    AddDisplayItem(displayItem);
                    _ = lastDisplayItems.TryRemove(displayItem.Guid, out _);
                }
            }
            MaxTargetFrameRateProfileByDisplayItem[profile.Guid] = tempMaxFps;
        }

        // Update internal state using the latest list of panels.
        private static void CheckUsedProfilesAndMaxFpsByPanel()
        {
            Dictionary<Guid, int> profileIndex = new();
            Dictionary<Guid, int> maxFrameRateByProfile = new();
            var lastEnabledProfiles = new ConcurrentDictionary<Guid, Profile>(EnabledProfiles);
            for (int i = 0; i < Profiles.Count; i++)
            {
                profileIndex.TryAdd(Profiles[i].Guid, i);
            }
            foreach (var vendor in PanelVendors)
            {
                // Check each panel vendor's settings and continue if enabled.
                string propName = $"{vendor}PanelMultiDeviceMode";
                var prop = typeof(Settings).GetProperty(propName);
                if (prop is null)
                {
                    Logger.Debug("Property {propName} was not found.", propName);
                    continue;
                }
                try
                {
                    var value = prop.GetValue(Settings);
                    if (value is bool result && !result) continue;
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Get error {propName}.", propName);
                    continue;
                }

                // For each enabled panel, add its referenced ProfileGuid and the maximum TargetFrameRate associated with that profile to the dictionary.
                propName = $"{vendor}PanelDevices";
                prop = typeof(Settings).GetProperty(propName);

                if (prop is null)
                {
                    Logger.Debug("Property {propName} was not found.", propName);
                    continue;
                }
                try
                {
                    var value = prop.GetValue(Settings);
                    if (value is null)
                    {

                        continue;
                    }
                    Logger.Debug("{propName} was found.", propName);

                    if (value is not System.Collections.IEnumerable devices)
                    {
                        Logger.Debug("{propName} is not enumerable.", propName);
                        continue;
                    }

                    foreach (var device in devices)
                    {
                        var type = device.GetType();

                        try
                        {
                            prop = type.GetProperty("Enabled");
                            if (prop is null)
                            {
                                Logger.Debug("{prop} was not found.", prop);
                                continue;
                            }
                            if (prop.GetValue(device) is not bool enabled || !enabled)
                            {
                                Logger.Debug("{device} was disabled.", device);
                                continue;
                            }

                            prop = type.GetProperty("ProfileGuid");
                            if (prop is null)
                            {
                                Logger.Debug("{device} {prop} was not found.", device, prop);
                                continue;
                            }
                            var profileGuid = prop.GetValue(device);
                            if (profileGuid is null)
                            {
                                Logger.Debug("{device} {prop} was null.", device, prop);
                                continue;
                            }
                            if (profileIndex.TryGetValue((Guid)profileGuid, out var index))
                            {
                                _ = EnabledProfiles.GetOrAdd((Guid)profileGuid, Profiles[index]);
                                _ = lastEnabledProfiles.TryRemove((Guid)profileGuid, out _);
                                Logger.Debug("{vendor} {panel} Enabled -> {ProfileGuid}", vendor, device, profileGuid);
                            }
                            else
                            {
                                Logger.Debug("{vendor} {panel} Enabled -> but, not fount {ProfileGuid}", vendor, device, profileGuid);
                                continue;
                            }

                            prop = type.GetProperty("TargetFrameRate");
                            if (prop is null)
                            {
                                Logger.Debug("{device} {prop} was not found.", device, prop);
                                continue;
                            }
                            var targetFrameRate = prop.GetValue(device);
                            if (targetFrameRate is null)
                            {
                                Logger.Debug("{device} {prop} was null.", device, prop);
                                continue;
                            }
                            if (maxFrameRateByProfile.TryGetValue((Guid)profileGuid, out var tempMaxFrameRate))
                            {
                                if ((int)targetFrameRate > tempMaxFrameRate)
                                {
                                    maxFrameRateByProfile[(Guid)profileGuid] = (int)targetFrameRate;
                                }
                            }
                            else
                            {
                                maxFrameRateByProfile[(Guid)profileGuid] = (int)targetFrameRate;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning(ex, "Get error {device} {propName}", device, propName);
                            continue;
                        }

                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Get error {propName}", propName);
                    continue;
                }
            }
            // Remove information for profiles that are no longer referenced.
            foreach (var profileId in lastEnabledProfiles.Keys)
            {
                _ = EnabledProfiles.TryRemove(profileId, out _);
                _ = MaxTargetFrameRateProfileByPanel.TryRemove(profileId, out _);
                _ = MaxTargetFrameRateProfileByDisplayItem.TryRemove(profileId, out _);
            }
            foreach (var profileId in EnabledProfiles.Keys)
            {
                // Update TargetFrameRate.
                try
                {
                    MaxTargetFrameRateProfileByPanel[profileId] = maxFrameRateByProfile[profileId];
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to update the panel frame rate for profile {ProfileGuid}.", profileId);
                    throw;
                }
            }
        }

        // Get the SensorId from a DisplayItem.
        private static string GetSensorIdFromDisplayItem(DisplayItem displayItem)
        {
            if (displayItem is null) return string.Empty;
            Logger.Debug("GetSensorIdFromDisplayItem : item class {class} , base class {baseclass}", displayItem.GetType().FullName, displayItem.GetType().BaseType!.FullName);
            return displayItem switch
            {
                ISensorItem sensorItem => GetSensorKey(sensorItem),
                _ => $"{displayItem.Guid}",
            };
        }

        private static string GetSensorKey(ISensorItem sensor)
        {
            return sensor switch
            {
                IHwInfoSensorItem hw when hw.SensorType == Enums.SensorType.HwInfo => $"{hw.HwInfoRemoteIndex}:{hw.Id}:{hw.Instance}:{hw.EntryId}",
                ILibreSensorItem libre when libre.SensorType == Enums.SensorType.Libre => $"{libre.LibreSensorId}",
                IPluginSensorItem plugin when plugin.SensorType == Enums.SensorType.Plugin => $"{plugin.PluginSensorId}",
                _ => string.Empty,
            };
        }

        // Get the target FPS required to update a DisplayItem.
        private static int GetFpsByDisplayItem(DisplayItem displayItem)
        {
            // Default to 0 FPS.
            int fps = MinFps;
            switch (displayItem)
            {
                case CalendarDisplayItem calendar:
                    // Dates rarely change, so manage them as infrequently updated DisplayItems.
                    AddLowFrequencyDicByDisplayItem(calendar);
                    break;
                case ClockDisplayItem clock:
                    // Check the format to determine the FPS (manage as an infrequently updated DisplayItem if it does not show sub-second precision).
                    // Check how many fractional-second digits are in the format.
                    int fractionalSecondDigits = GetFormatDigits(clock.Format, 'f');
                    // Check whether the format includes seconds.
                    int secondDigits = GetFormatDigits(clock.Format, 's');
                    if (fractionalSecondDigits >= 2)
                    {
                        // Use MaxFps when there are two or more fractional-second digits.
                        fps = MaxFps;
                    }
                    else if (fractionalSecondDigits == 1)
                    {
                        // Use 10 FPS when there is one fractional-second digit.
                        fps = 10;
                    }
                    else if (secondDigits > 0)
                    {
                        // Use 1 FPS if seconds are included.
                        fps = 1;
                    }
                    else
                    {
                        fps = MinFps;
                    }
                    if (fps == MinFps)
                    {
                        AddLowFrequencyDicByDisplayItem(clock);
                    }
                    else
                    {
                        RemoveLowFrequencyDicByDisplayItem(clock);
                    }
                    break;
                case ISensorItem sensor:
                    // Handles sensor-dependent display items and determines their target FPS:
                    // ChartDisplayItem (BarDisplayItem, GraphDisplayItem, DonutDisplayItem),
                    // GaugeDisplayItem, HttpImageDisplayItem, SensorDisplayItem, and SensorImageDisplayItem.
                    if (displayItem is ChartDisplayItem chart)
                    {
                        if (displayItem is GraphDisplayItem)
                        {
                            // Clamp the value to prevent unexpected values if the configuration file is edited manually; the GUI already enforces this range.
                            // MinTargetGraphUpdateRate / MaxTargetGraphUpdateRate are the minimum and maximum values configurable in the GUI.
                            var targetGraphUpdateRate = Math.Clamp(ConfigModel.Instance.Settings.TargetGraphUpdateRate, MinTargetGraphUpdateRate, MaxTargetGraphUpdateRate);
                            fps = (int)Math.Ceiling(1000.0 / targetGraphUpdateRate);
                            break;
                        }
                        if (chart.Animation)
                        {
                            // BarDisplayItem and DonutDisplayItem use different target FPS values depending on whether animation is enabled.
                            // When animation is enabled, update at the maximum FPS (MaxFps).
                            fps = MaxFps;
                            break;
                        }
                    }
                    // Sensors have different collection intervals in HWiNFO, LibreHardwareMonitor, and plugins, so handle each type separately.
                    switch (sensor)
                    {
                        case IHwInfoSensorItem hw when hw.SensorType == Enums.SensorType.HwInfo:
                            fps = (int)Math.Ceiling(1000.0 / HWPoolIntervalMilliseconds);
                            break;
                        case ILibreSensorItem libre when libre.SensorType == Enums.SensorType.Libre:
                            fps = (int)Math.Ceiling(1000.0 / LibrePoolIntervalMilliseconds);
                            break;
                        case IPluginSensorItem plugin when plugin.SensorType == Enums.SensorType.Plugin:
                            // Plugin sensors update according to the plugin's schedule, so this class does not determine their FPS.
                            break;
                        default:
                            // Unsupported sensor type (add a case here when a new interface is introduced).
                            break;
                    }
                    break;
                case ShapeDisplayItem shape:
                    // ShapeDisplayItem update frequency depends on the gradient animation speed.
                    if (shape.GradientAnimationSpeed == 0)
                    {
                        // Use 0 FPS when animation is disabled.
                    }
                    else
                    {
                        // When animation is enabled, set FPS according to GradientAnimationSpeed.
                        // GradientAnimationSpeed can be set from 1 to 3600, but clamp it to MaxFps because values above MaxFps (60) exceed the actual rendering capability.
                        fps = Math.Clamp(shape.GradientAnimationSpeed, 1, MaxFps);
                    }
                    break;
                default:
                    // HttpImageDisplayItem and SensorImageDisplayItem inherit ImageDisplayItem, 
                    // so determine their FPS here rather than in a dedicated switch case.
                    if (displayItem is ImageDisplayItem image)
                    {
                        switch (image.Type)
                        {
                            case ImageDisplayItem.ImageType.URL:
                                if (image.RefreshInterval == 0)
                                {
                                    // A refresh interval of 0 disables periodic updates, so keep the FPS at MinFps.
                                    RemoveLowFrequencyDicByDisplayItem(image);
                                }
                                else
                                {
                                    // Values of 1 or greater may update at intervals of one second or more, so manage them as infrequently updated DisplayItems.
                                    // Increment the associated profile version at the configured interval to trigger rendering and issue a request.
                                    AddLowFrequencyDicByDisplayItem(image);
                                }

                                break;
                            default:
                                fps = image.TargetFps;
                                break;
                        }
                        break;

                    }
                    // TextDisplayItem, GroupDisplayItem, and GuidDisplayItem update only when their settings change.
                    // A settings change triggers a redraw, so no additional handling is needed here.
                    // Consider adding a case here when a new DisplayItem type is introduced.
                    break;
            }
            return fps;
        }

        // Add a DisplayItem to the collection of infrequently updated items.
        private static void AddLowFrequencyDicByDisplayItem(DisplayItem displayItem)
        {
            _ = LowUpdateFrequencyDisplayItemIds.GetOrAdd(displayItem.Guid, displayItem);
            var currentTimeFrequency = Stopwatch.GetTimestamp();
            switch (displayItem)
            {
                case TextDisplayItem text:
                    try
                    {
                        LowUpdateFrequencyDisplayItemStringValues[text.Guid] = text.EvaluateText();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Failed to initialize low-frequency text DisplayItem {ProfileGuid}/{DisplayItemGuid} ({Name}).", text.ProfileGuid, text.Guid, text.Name);
                        throw;
                    }
                    break;
                case ImageDisplayItem image:
                    // For URLs, initialize with the current time so requests can be made at the configured interval.
                    // Use GetOrAdd to preserve the original interval baseline.
                    if (image.Type == ImageDisplayItem.ImageType.URL)
                    {
                        _ = LowUpdateFrequencyDisplayItemLongValues.GetOrAdd(image.Guid, currentTimeFrequency);
                    }
                    break;
            }
        }

        // Remove a DisplayItem from the collection of infrequently updated items.
        private static void RemoveLowFrequencyDicByDisplayItem(DisplayItem displayItem)
        {
            _ = LowUpdateFrequencyDisplayItemIds.TryRemove(displayItem.Guid, out _);
            _ = LowUpdateFrequencyDisplayItemStringValues.TryRemove(displayItem.Guid, out _);
            _ = LowUpdateFrequencyDisplayItemLongValues.TryRemove(displayItem.Guid, out _);
        }


        // Get the maximum number of consecutive occurrences of the specified format specifier (s or f) in a time format string.
        static int GetFormatDigits(string format, char targetChar)
        {
            if (string.IsNullOrEmpty(format)) return 0;

            // Create the character pattern to search for.
            string targetPattern;
            if (char.ToLower(targetChar) == 'f')
            {
                // For fractional seconds, match both uppercase F and lowercase f.
                targetPattern = "([fF]+)";
            }
            else
            {
                // For seconds (s) and other characters.
                targetPattern = $"({char.ToLower(targetChar)}+)";
            }

            // Build the regular expression pattern.
            // 1. '[^']*'       : Literal enclosed in single quotes.
            // 2. "[^"]*"       : Literal enclosed in double quotes.
            // 3. \\.           : One character escaped with a backslash.
            // 4. targetPattern : Consecutive occurrences of the target character.
            string pattern = $@"'[^']*'|""[^""]*""|\\.|{targetPattern}";

            MatchCollection matches = Regex.Matches(format, pattern);

            int maxLength = 0;
            foreach (Match match in matches)
            {
                Group targetGroup = match.Groups[1];
                if (targetGroup.Success)
                {
                    if (targetGroup.Length > maxLength)
                    {
                        maxLength = targetGroup.Length;
                    }
                }
            }
            return maxLength;
        }
    }
}
