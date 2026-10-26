using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace InfoPanel.Editing;

/// <summary>
/// The engine's profiles: the list (read from the store once, then kept in memory) and one
/// <see cref="ProfileDocument"/> per profile, opened on first use. Read side only; M5 adds create, duplicate,
/// delete, import, export and saving.
/// </summary>
public sealed class Workspace
{
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<Guid, ProfileDocument> _documents = new();
    private List<Profile>? _profiles;

    public Workspace(ProfileStore store)
    {
        Store = store;
    }

    public ProfileStore Store { get; }

    /// <summary>Every profile in list order; open documents report their own revision and dirty state.</summary>
    public IReadOnlyList<ProfileSummary> ListProfiles()
    {
        var profiles = LoadProfiles();
        return profiles
            .Select(p => _documents.TryGetValue(p.Guid, out var doc)
                ? doc.ToSummary()
                : new ProfileSummary(p.Guid, p.Name, p.Width, p.Height, 1, false))
            .ToList();
    }

    /// <summary>The profile's document, loading its items on first use; null when there is no such profile.</summary>
    public ProfileDocument? Open(Guid profileId)
    {
        if (_documents.TryGetValue(profileId, out var doc))
            return doc;

        var profile = LoadProfiles().FirstOrDefault(p => p.Guid == profileId);
        if (profile == null)
            return null;

        lock (_lock)
        {
            if (_documents.TryGetValue(profileId, out doc))
                return doc;

            doc = new ProfileDocument(profile, Store.LoadItems(profile));
            _documents[profileId] = doc;
            return doc;
        }
    }

    /// <summary>
    /// When the data folder has no profiles, adds a simple starter profile and returns true. M5 replaces it with
    /// <c>ExampleProfiles.WideBar()</c>.
    /// </summary>
    public bool EnsureStarterProfile()
    {
        lock (_lock)
        {
            if (LoadProfiles().Count > 0)
                return false;

            var (profile, items) = CreateStarterProfile();
            Store.AddProfile(profile, items);
            _profiles = null;
            return true;
        }
    }

    private List<Profile> LoadProfiles()
    {
        lock (_lock)
            return _profiles ??= Store.LoadProfiles();
    }

    internal static (Profile Profile, List<DisplayItem> Items) CreateStarterProfile()
    {
        var profile = new Profile("Welcome", 800, 480)
        {
            BackgroundColor = "#FF101418",
            Color = "#FFFFFFFF",
            Font = FontDefaults.Family,
            FontSize = 20,
        };

        const string amber = "#FFFFB000";
        const string white = "#FFFFFFFF";
        const string track = "#FF2A2A2A";

        static void Bind(DisplayItem item, string mockId)
        {
            var def = MockSensorCatalog.ById(mockId)!;
            var sensor = (IPluginSensorItem)item;
            sensor.SensorName = def.Name;
            sensor.SensorType = SensorType.Plugin;
            sensor.PluginSensorId = def.Key.Id;
        }

        var title = new TextDisplayItem("InfoPanel 2.0", profile) { X = 40, Y = 28, FontSize = 30, Bold = true, Color = amber };
        var clock = new ClockDisplayItem("Clock", profile) { X = 40, Y = 84, FontSize = 48, Bold = true, Color = white };

        var cpuTemp = new SensorDisplayItem("CPU (Tctl/Tdie)", profile) { X = 40, Y = 190, FontSize = 28, Color = white };
        Bind(cpuTemp, "cpu.temp");

        var gpuLoad = new BarDisplayItem("GPU Core Load", profile) {
            X = 400, Y = 200, Width = 360, Height = 24, Color = amber, Gradient = false, BackgroundColor = track, Frame = false,
        };
        Bind(gpuLoad, "gpu.load");

        var cpuLoad = new GraphDisplayItem("Total CPU Usage", profile, GraphDisplayItem.GraphType.LINE)
        {
            X = 40, Y = 270, Width = 720, Height = 170, Color = amber, FillColor = "#33FFB000", BackgroundColor = "#FF1B1B1B", Frame = false,
        };
        Bind(cpuLoad, "cpu.load");

        return (profile, [title, clock, cpuTemp, gpuLoad, cpuLoad]);
    }
}
