using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using InfoPanel.Documents;
using InfoPanel.Drawing;
using InfoPanel.Editing;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using SkiaSharp;

namespace InfoPanel.Import;

/// <summary>
/// What one import request reads with: the workspace (blobs are stored through it, with its limits), the sensors (for
/// each binding's status now), the fonts this engine has, the import limits, and the request's byte budget.
/// </summary>
public sealed class V1ReadContext
{
    public V1ReadContext(Workspace workspace, SensorHub sensors, ImportLimits? limits = null, Func<string, bool>? fontAvailable = null)
    {
        Workspace = workspace;
        Sensors = sensors;
        Limits = limits ?? ImportLimits.Default;
        Budget = new ImportBudget(Limits.ExpandedBytes);
        FontAvailable = fontAvailable ?? InstalledFonts();
    }

    public Workspace Workspace { get; }
    public SensorHub Sensors { get; }
    public ImportLimits Limits { get; }

    /// <summary>The request's budget of bytes read; one context per request.</summary>
    public ImportBudget Budget { get; }

    /// <summary>Whether a font family is among the engine's fonts (as <c>GET /api/fonts</c> lists them).</summary>
    public Func<string, bool> FontAvailable { get; }

    /// <summary>The fonts the engine draws with: its registered ones and the system's, by family, ignoring case.</summary>
    public static Func<string, bool> InstalledFonts()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in FontRegistry.GetFamilies().Concat(SKFontManager.Default.GetFontFamilies()))
        {
            if (!string.IsNullOrWhiteSpace(family) && !family.StartsWith('.'))
                families.Add(family);
        }
        return families.Contains;
    }
}

/// <summary>
/// Reads one 1.x profile into its envelope (<see cref="V1Profile"/>), installing nothing: the profile model's members,
/// the items as the 1.x item JSON (<see cref="ItemJson.ToNode"/>, gauge frames included), the guides from the raw XML,
/// the assets stored as blobs, and diagnostics from the raw XML. A 1.x archive (<see cref="ReadArchive"/>) and a 1.x
/// folder profile (<see cref="V1Folder"/>) both end here.
/// </summary>
public static class V1Reader
{
    public const string FolderSource = "folder", ArchiveSource = "archive";

    private const string ProfileEntry = "Profile.xml", ItemsEntry = "DisplayItems.xml";
    private const string GuideType = "GuideDisplayItem";

    /// <summary>
    /// Reads a 1.x <c>.infopanel</c> archive (a seekable stream: the body in a temporary file) into its envelope.
    /// <c>Profile.xml</c> and <c>DisplayItems.xml</c> match case-insensitively, asset entries start <c>assets/</c> or
    /// <c>assets\</c>, the later of two entries with one name wins (1.4.x extracted with overwrite), and any other entry is
    /// ignored (<c>import.entry</c>). The archive's own <c>Profile.xml</c> is what 1.4.x exported: no window placement.
    /// </summary>
    /// <exception cref="ImportRefusal">
    /// The archive breaks a guard (<c>archive.*</c>), is a 2.0 profile archive (<c>archive.v2</c>), has no
    /// <c>Profile.xml</c> or <c>DisplayItems.xml</c> (<c>import.archive</c>), or its <c>Profile.xml</c> can't be read
    /// (<c>import.xml</c>).
    /// </exception>
    public static V1Profile ReadArchive(Stream archive, V1ReadContext context)
    {
        using var reader = ArchiveReader.Open(archive, context.Limits, context.Budget);
        var diagnostics = new ImportDiagnostics(context.Limits.Diagnostics);

        ArchiveEntry? profileEntry = null, itemsEntry = null;
        var assetEntries = new List<ArchiveEntry>();
        var others = new List<ArchiveEntry>();
        foreach (var entry in reader.Entries)
        {
            if (entry.Segments.Count == 1 && !entry.IsDirectory && entry.Name.Equals(ProfileEntry, StringComparison.OrdinalIgnoreCase))
                profileEntry = entry;
            else if (entry.Segments.Count == 1 && !entry.IsDirectory && entry.Name.Equals(ItemsEntry, StringComparison.OrdinalIgnoreCase))
                itemsEntry = entry;
            else if (entry.Segments.Count >= 1 && entry.Segments[0].Equals("assets", StringComparison.OrdinalIgnoreCase)
                     && (entry.Segments.Count > 1 || entry.IsDirectory))
                assetEntries.Add(entry);
            else
                others.Add(entry);
        }

        if (profileEntry is null || itemsEntry is null)
        {
            if (reader.Entries.Any(e => e.Name == ProfileArchive.DocumentEntry))
                throw new ImportRefusal("archive.v2", "This is a 2.0 profile archive (.infopanel2), not a 1.x .infopanel file: import it as a profile archive.");
            throw new ImportRefusal("import.archive", $"The archive has no {ProfileEntry} or no {ItemsEntry}: it isn't a 1.x profile export.");
        }

        var profileBytes = reader.Read(profileEntry, context.Limits.ProfileXmlBytes)
            ?? throw ImportRefusal.Entry("import.xml", profileEntry.Name, $"{profileEntry.Name} is larger than {context.Limits.ProfileXmlBytes} bytes.");
        Profile profile;
        var profileReport = new XmlReadReport();
        try
        {
            profile = ProfileSerializer.ReadProfile(SafeXml.Load(profileBytes, XmlLimits.From(context.Limits)), profileReport);
        }
        catch (XmlRefusedException ex)
        {
            throw ImportRefusal.Entry("import.xml", profileEntry.Name, $"{profileEntry.Name} can't be read: {ex.Message}", ex);
        }
        catch (InvalidDataException ex)
        {
            throw ImportRefusal.Entry("import.xml", profileEntry.Name, $"{profileEntry.Name} can't be read: {ex.Message}", ex);
        }

        var itemsBytes = reader.Read(itemsEntry, context.Limits.ItemsXmlBytes);
        var items = new ItemsInput(itemsEntry.Name, itemsBytes, Missing: false, OverLimit: itemsBytes is null);

        var assets = new V1ArchiveAssets(reader, assetEntries, diagnostics);
        var envelope = Read(ArchiveSource, profile, profileReport, profileEntry.Name, items, assets, context, diagnostics);

        foreach (var entry in others.Where(e => !e.IsDirectory))
            diagnostics.Add(ImportDiagnostics.Info, "import.entry", "", $"The entry '{entry.Name}' isn't part of a 1.x profile export and is ignored.", entry.Name);
        if (assets.Unused > 0)
            diagnostics.Add(ImportDiagnostics.Info, "import.asset.unused", "/assets",
                $"{assets.Unused} asset file(s) in the archive aren't used by any item and aren't stored.");

        return envelope with { Diagnostics = diagnostics.ToList() };
    }

    /// <summary>A profile's items file: its bytes, or that it is missing, over the limit, or can't be read (<see cref="Problem"/>).</summary>
    internal sealed record ItemsInput(string File, byte[]? Bytes, bool Missing, bool OverLimit, string? Problem = null);

    /// <summary>The envelope of <paramref name="profile"/> with its items and assets; diagnostics go to <paramref name="diagnostics"/>.</summary>
    internal static V1Profile Read(string source, Profile profile, XmlReadReport? profileReport, string profileFile, ItemsInput itemsInput,
        IV1AssetSource assetSource, V1ReadContext context, ImportDiagnostics diagnostics)
    {
        // The profile.
        if (profileReport is not null)
        {
            foreach (var node in profileReport.Unknown.Where(n => ReferenceEquals(n.Owner, profile) || ReferenceEquals(n.Owner, profile.TargetWindow)))
                diagnostics.Add(ImportDiagnostics.Warning, "import.element", "/profile",
                    $"The profile's {(node.IsAttribute ? "attribute" : "element")} '{node.Name}' isn't one 1.x's reader knows; it is ignored.", profileFile, node.Line);
        }
        if (profile.Width > Workspace.MaxDimension)
            diagnostics.Add(ImportDiagnostics.Warning, "import.size", "/profile/width", $"The width {profile.Width} is over the largest canvas side, {Workspace.MaxDimension}.", profileFile);
        if (profile.Height > Workspace.MaxDimension)
            diagnostics.Add(ImportDiagnostics.Warning, "import.size", "/profile/height", $"The height {profile.Height} is over the largest canvas side, {Workspace.MaxDimension}.", profileFile);

        var fonts = new Dictionary<string, (string Path, int Count)>(StringComparer.OrdinalIgnoreCase);
        void UseFont(string? family, string path)
        {
            if (string.IsNullOrWhiteSpace(family) || context.FontAvailable(family))
                return;
            fonts[family] = fonts.TryGetValue(family, out var seen) ? (seen.Path, seen.Count + 1) : (path, 1);
        }
        UseFont(profile.Font, "/profile/font");

        // The items.
        var (items, report) = ReadItems(itemsInput, context.Limits, diagnostics);
        var guides = new List<V1Guide>();
        if (report is not null)
        {
            foreach (var skipped in report.Skipped.OrderBy(s => s.Line ?? int.MaxValue))
            {
                var container = "/items" + (skipped.Container ?? "");
                if (skipped.Type is null)
                    diagnostics.Add(ImportDiagnostics.Info, "import.nil", container, "An empty (xsi:nil) item is left out, as 1.x leaves it.", itemsInput.File, skipped.Line);
                else if (skipped.Type == GuideType)
                    guides.Add(Guide(skipped.Element, skipped.Container is { Length: > 0 } inGroup ? "/items" + inGroup : null));
                else
                    diagnostics.Add(ImportDiagnostics.Warning, "import.itemType", container,
                        $"An item of the type '{skipped.Type}', which isn't one of the 14 kinds, is left out.", itemsInput.File, skipped.Line);
            }

            foreach (var group in report.Unknown.GroupBy(n => (Item: n.Owner as DisplayItem, n.Name, n.IsAttribute)))
            {
                var first = group.First();
                var item = group.Key.Item;
                var at = item is not null && report.PathOf(item) is { } itemPath ? "/items" + itemPath : "/items";
                diagnostics.Add(ImportDiagnostics.Warning, "import.element", at,
                    $"The {(group.Key.IsAttribute ? "attribute" : "element")} '{group.Key.Name}' isn't one 1.x's reader knows; it is ignored and the item still loads.",
                    itemsInput.File, first.Line ?? (item is null ? null : report.LineOf(item)));
            }
        }

        foreach (var item in items)
            item.SetProfile(profile);

        string PathOf(DisplayItem item) => report?.PathOf(item) is { } p ? "/items" + p : "/items";
        int? LineOf(DisplayItem item) => report?.LineOf(item);

        var nodes = items.Select(item => ItemJson.ToNode(item, context.Sensors)).ToList();
        foreach (var item in ItemTree.Walk(items))
        {
            if (item is TextDisplayItem text)
                UseFont(text.Font, PathOf(item) + "/props/font");
            if (SensorKeys.Of(item) is { } key && context.Sensors.GetStatus(key) is var status && status is SensorStatus.Unavailable or SensorStatus.Missing)
                diagnostics.Add(ImportDiagnostics.Info, "import.sensor", PathOf(item),
                    $"'{item.Name}' is bound to {key}, which this engine has {(status == SensorStatus.Missing ? "no sensor for" : "no reading of")} now; it stays bound.",
                    itemsInput.File, LineOf(item));
        }

        foreach (var (family, (path, count)) in fonts)
            diagnostics.Add(ImportDiagnostics.Warning, "import.font", path,
                $"The font '{family}' ({count} use{(count == 1 ? "" : "s")}) isn't among this engine's fonts: text in it draws in Inter, and its widths change.");

        // The assets.
        var assets = V1Assets.Resolve(V1Assets.Uses(items, PathOf), assetSource, context.Workspace, context.Budget, diagnostics);

        return new V1Profile
        {
            Source = source,
            Id = profile.Guid.ToString(),
            Profile = ItemJson.ProfileProps(profile),
            TargetWindow = profile.TargetWindow is { } window
                ? new V1TargetWindow { X = window.X, Y = window.Y, Width = window.Width, Height = window.Height, DeviceName = window.DeviceName }
                : null,
            Items = nodes,
            Guides = guides,
            Assets = assets,
            Diagnostics = diagnostics.ToList(),
        };
    }

    /// <summary>Reads the items, reporting why there are none when they can't be read.</summary>
    private static (List<DisplayItem> Items, XmlReadReport? Report) ReadItems(ItemsInput input, ImportLimits limits, ImportDiagnostics diagnostics)
    {
        if (input.Missing)
        {
            diagnostics.Add(ImportDiagnostics.Info, "import.items", "/items", "The profile has no items file: it has no items, as 1.x loads it.", input.File);
            return ([], null);
        }
        if (input.Problem is { } problem)
        {
            diagnostics.Add(ImportDiagnostics.Error, "import.items", "/items", $"The items file can't be read: {problem} No items are read.", input.File);
            return ([], null);
        }
        if (input.OverLimit || input.Bytes is null)
        {
            diagnostics.Add(ImportDiagnostics.Error, "import.items", "/items", $"The items file is larger than {limits.ItemsXmlBytes} bytes: no items are read.", input.File);
            return ([], null);
        }

        XDocument document;
        try
        {
            document = SafeXml.Load(input.Bytes, XmlLimits.From(limits));
        }
        catch (XmlRefusedException ex)
        {
            diagnostics.Add(ImportDiagnostics.Error, "import.xml", "/items", $"The items file can't be read: {ex.Message} No items are read.", input.File, ex.Line);
            return ([], null);
        }

        var report = new XmlReadReport();
        try
        {
            return (ProfileSerializer.ReadItems(document, report), report);
        }
        catch (InvalidDataException ex)
        {
            diagnostics.Add(ImportDiagnostics.Error, "import.items", "/items", $"The items file isn't a 1.x item list: {ex.Message} No items are read.", input.File);
            return ([], null);
        }
    }

    /// <summary>
    /// A 1.4.x guide from its element; an absent or unreadable member gets 1.4.x's default (the name
    /// <c>DisplayItem</c>, which 1.4.x's parameterless <c>DisplayItem()</c> sets). <paramref name="container"/> is the
    /// group's children list it sat in, or null at the top level.
    /// </summary>
    private static V1Guide Guide(XElement element, string? container)
    {
        int Int(string name, int fallback) =>
            element.Element(name)?.Value is { } text && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        bool Bool(string name, bool fallback)
        {
            if (element.Element(name)?.Value is not { } text)
                return fallback;
            try { return XmlConvert.ToBoolean(text.Trim()); }
            catch (FormatException) { return fallback; }
        }

        return new V1Guide
        {
            X = Int("X", 100),
            Y = Int("Y", 100),
            Length = Int("Length", 200),
            Color = element.Element("Color")?.Value ?? "#FF00FFFF",
            Rotation = Int("Rotation", 0),
            Name = element.Element("Name")?.Value ?? "DisplayItem",
            Hidden = Bool("Hidden", false),
            Locked = Bool("IsLocked", false),
            Container = container,
        };
    }
}
