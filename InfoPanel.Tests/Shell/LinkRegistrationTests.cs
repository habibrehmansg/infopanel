using System.Text.RegularExpressions;
using System.Xml.Linq;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// The <c>infopanel:</c> link's registration with the OS (docs/2.0/desktop-shell.md, "Opening the editor"): the Mac app
/// bundle's <c>Info.plist</c> template declares the scheme, and the Windows installer writes the protocol's key per
/// machine, its command passing the link as the only argument, and removes it at uninstall.
/// </summary>
public sealed class LinkRegistrationTests
{
    [Fact]
    public void ThePlistTemplate_DeclaresTheInfopanelScheme_AsTheBundlesOneUrlType()
    {
        var plist = XDocument.Load(Path.Combine(LiveEngine.RepoRoot(), "packaging", "macos", "Info.plist"));
        var top = plist.Root!.Element("dict")!;

        var types = Assert.IsType<XElement>(ValueOf(top, "CFBundleURLTypes"));
        Assert.Equal("array", types.Name.LocalName);
        var type = Assert.Single(types.Elements());
        Assert.Equal("dict", type.Name.LocalName);
        Assert.Equal("net.infopanel.InfoPanel", ValueOf(type, "CFBundleURLName")!.Value);
        var schemes = ValueOf(type, "CFBundleURLSchemes")!;
        Assert.Equal("array", schemes.Name.LocalName);
        Assert.Equal(["infopanel"], schemes.Elements().Select(e => e.Value));
    }

    [Fact]
    public void TheInstaller_RegistersTheLinkPerMachine_WithTheLinkAsTheOnlyArgument_AndRemovesItAtUninstall()
    {
        var script = File.ReadAllText(Path.Combine(LiveEngine.RepoRoot(), "packaging", "windows", "InfoPanel.iss"));
        var block = Regex.Match(script, @"^\[Registry\]\r?\n(?<lines>(?:(?!\[).*\r?\n)*)", RegexOptions.Multiline);
        Assert.True(block.Success, "The installer has no [Registry] section.");
        var entries = block.Groups["lines"].Value.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith(';'))
            .ToList();

        Assert.Equal(
        [
            @"Root: HKA; Subkey: ""Software\Classes\infopanel""; ValueType: string; ValueName: """"; ValueData: ""URL:InfoPanel""; Flags: uninsdeletekey",
            @"Root: HKA; Subkey: ""Software\Classes\infopanel""; ValueType: string; ValueName: ""URL Protocol""; ValueData: """"",
            @"Root: HKA; Subkey: ""Software\Classes\infopanel\DefaultIcon""; ValueType: string; ValueName: """"; ValueData: """"""{app}\{#AppExeName}"""",0""",
            @"Root: HKA; Subkey: ""Software\Classes\infopanel\shell\open\command""; ValueType: string; ValueName: """"; ValueData: """"""{app}\{#AppExeName}"""" """"%1""""""",
        ], entries);
        // Per machine: HKA is HKLM only in an administrative install.
        Assert.Matches(@"(?m)^PrivilegesRequired=admin\r?$", script);
    }

    /// <summary>The value after <paramref name="key"/>'s <c>key</c> element in a plist <c>dict</c>, or null.</summary>
    private static XElement? ValueOf(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();
}
