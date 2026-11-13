// Prints the USB devices panel packages match, one line a match: "panel-match <vvvv:pppp> <usb|serial|hid>" (the
// prefix tells them from anything the dotnet CLI itself prints). packaging/linux/build.sh makes the udev rules from
// these lines; it needs only the .NET SDK, which the build needs anyway.
//
// Each panel.json is read by the engine's own reader, InfoPanel.Core's PanelPackages.ReadManifest, the one the engine
// loads a package with: a package it would skip (a manifest it refuses), one built for a panel SDK it doesn't run, or one
// declaring a family another package given here, or BeadaPanel, already has, stops it with the engine's own message. It
// is read by the panel SDK's PanelManifest too, as its driver reads it, which must accept it and find the same matches.
// The matches are those the engine's reader finds: only each families[].match[] entry's own members, its transport
// usb when it names none.
//
//   dotnet run --file packaging/linux/panel-matches.cs -- <panel.json>...
//
// Exit 0 with the lines; 1, saying why on stderr, when a package would be skipped or isn't run, or the two readers
// disagree; 2 with no file.
#:project ../../InfoPanel.Core/InfoPanel.Core.csproj
#:property Nullable=enable

using System.Globalization;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;

if (args.Length == 0)
{
    Console.Error.WriteLine("panel-matches.cs: give one or more panel.json files");
    return 2;
}
// Family ids are the engine's to hand out in order: BeadaPanel's, built in, then each package's (PanelPackages.Find).
var taken = new Dictionary<string, string>(StringComparer.Ordinal) { [SavedPanel.BeadaPanelFamily] = "built into the engine" };
var lines = new List<string>();
foreach (var path in args)
{
    var full = Path.GetFullPath(path);
    if (Path.GetFileName(full) != PanelPackages.ManifestName)
    {
        Console.Error.WriteLine($"panel-matches.cs: {path} isn't a {PanelPackages.ManifestName}");
        return 1;
    }
    var folder = Path.GetFileName(Path.GetDirectoryName(full)!);
    if (PanelPackages.ReadManifest(Path.GetDirectoryName(full)!, out var error) is not { } manifest)
    {
        // The engine's own words: "Skipped the panel package {Folder}: its panel.json {Error}".
        Console.Error.WriteLine($"panel-matches.cs: the engine would skip the panel package {folder}: its {PanelPackages.ManifestName} {error} ({path})");
        return 1;
    }
    if (PanelPackages.SdkProblem(manifest.Sdk) is { } sdk)
    {
        Console.Error.WriteLine($"panel-matches.cs: the engine wouldn't run the panel package {folder}: {sdk} ({path})");
        return 1;
    }
    foreach (var family in manifest.Families)
    {
        if (taken.TryGetValue(family.Id, out var earlier))
        {
            Console.Error.WriteLine($"panel-matches.cs: the engine wouldn't run the panel package {folder}: its family {family.Id} is "
                + (earlier == "built into the engine" ? earlier : $"the panel package {earlier}'s, earlier in the list") + $" ({path})");
            return 1;
        }
        taken[family.Id] = folder;
    }

    // Its driver reads the same file with the panel SDK's reader: it must accept it and find the same matches.
    PanelManifest driver;
    try
    {
        driver = PanelManifest.Read(full);
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine($"panel-matches.cs: the panel package {folder}'s driver would refuse its {PanelPackages.ManifestName}: {ex.Message}");
        return 1;
    }
    var engineMatches = manifest.Families.Select(f => (f.Id, Matches: f.Matches)).ToList();
    var driverMatches = driver.Families.Select(f => (f.Id, Matches: f.Matches)).ToList();
    if (engineMatches.Count != driverMatches.Count
        || engineMatches.Zip(driverMatches).Any(p => p.First.Id != p.Second.Id || !p.First.Matches.SequenceEqual(p.Second.Matches)))
    {
        Console.Error.WriteLine($"panel-matches.cs: the engine and the panel package {folder}'s driver read different families or matches from its {PanelPackages.ManifestName} ({path})");
        return 1;
    }

    foreach (var family in manifest.Families)
    foreach (var match in family.Matches)
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"panel-match {match.VendorId:x4}:{match.ProductId:x4} {PanelPackages.NameOf(match.Transport)}"));
}
// Printed only once every package has passed: a refused one prints nothing a caller could take for a match.
foreach (var line in lines)
    Console.WriteLine(line);
return 0;
