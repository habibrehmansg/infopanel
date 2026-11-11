using System.Text;
using InfoPanel.Panels.Sdk;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// A driver's own reading of its <c>panel.json</c> (<see cref="PanelManifest"/>): its families, matches and models, each
/// model's JSON kept whole so the driver reads the detection keys only it knows.
/// </summary>
public sealed class PanelManifestTests
{
    // The virtual panel package's panel.json, as that package has it.
    private static readonly string VirtualPanel = Path.Combine(AppContext.BaseDirectory, "Fixtures", "panels", "virtual", "panel.json");

    private const string WithDetectionKeys = """
        {
          "format": "infopanel.panel-package/1",
          "sdk": "1.0",
          "families": [
            {
              "id": "example",
              "name": "Example panels",
              "match": [{ "usb": "87ad:70db", "transport": "hid", "interface": 1 }, { "usb": "0416:5302" }],
              "models": [
                { "id": "frozen-warframe", "name": "Frozen Warframe", "width": 240, "height": 320, "v1Model": "FrozenWarframe", "pm": 65, "sub": [1, 2] },
                { "id": "plain", "name": "Plain", "width": 480, "height": 480 }
              ],
              "detect": [{ "vendor": "acme" }]
            }
          ]
        }
        """;

    [Fact]
    public void The_virtual_panels_manifest_reads()
    {
        var manifest = PanelManifest.Read(VirtualPanel);

        Assert.Equal("1.0", manifest.Sdk);
        var family = Assert.Single(manifest.Families);
        Assert.Equal("virtual", family.Id);
        Assert.Equal("Virtual panel", family.Name);
        Assert.Equal([new PanelMatch(0x1209, 0x0001)], family.Matches);
        var model = Assert.Single(family.Models);
        Assert.Equal(("480x320", "Virtual panel 480 × 320", 480, 320, (string?)null), (model.Id, model.Name, model.Width, model.Height, model.V1Model));
        Assert.Same(family, manifest.Family("virtual"));
    }

    [Fact]
    public void A_models_members_the_engine_doesnt_know_are_kept_for_the_driver()
    {
        var manifest = PanelManifest.Parse(Encoding.UTF8.GetBytes(WithDetectionKeys));
        GC.Collect(); // the JSON is the manifest's own, not a view of a document since let go

        var family = manifest.Family("example");
        Assert.Equal([new PanelMatch(0x87ad, 0x70db, 1, PanelTransport.Hid), new PanelMatch(0x0416, 0x5302)], family.Matches);
        var model = family.Models[0];
        Assert.Equal("FrozenWarframe", model.V1Model);
        Assert.Equal(65, model.Json.GetProperty("pm").GetInt32());
        Assert.Equal([1, 2], model.Json.GetProperty("sub").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("acme", family.Json.GetProperty("detect")[0].GetProperty("vendor").GetString());
        Assert.False(family.Models[1].Json.TryGetProperty("pm", out _));
    }

    [Fact]
    public void The_virtual_panels_manifest_with_a_member_added_to_its_model_keeps_it()
    {
        var text = File.ReadAllText(VirtualPanel).Replace("\"height\": 320 }", "\"height\": 320, \"pm\": 65 }", StringComparison.Ordinal);
        Assert.Contains("\"pm\": 65", text);

        var model = Assert.Single(PanelManifest.Parse(Encoding.UTF8.GetBytes(text)).Family("virtual").Models);

        Assert.Equal(65, model.Json.GetProperty("pm").GetInt32());
    }

    [Theory]
    [InlineData("{\"format\": \"infopanel.panel-package/2\", \"sdk\": \"1.0\", \"families\": []}")]
    [InlineData("{\"format\": \"infopanel.panel-package/1\", \"sdk\": \"1\", \"families\": [{\"id\": \"a\", \"name\": \"A\", \"match\": [{\"usb\": \"1234:5678\"}]}]}")]
    [InlineData("{\"format\": \"infopanel.panel-package/1\", \"sdk\": \"1.0\", \"families\": [{\"id\": \"A\", \"name\": \"A\", \"match\": [{\"usb\": \"1234:5678\"}]}]}")]
    [InlineData("{\"format\": \"infopanel.panel-package/1\", \"sdk\": \"1.0\", \"families\": [{\"id\": \"a\", \"name\": \"A\", \"match\": [{\"usb\": \"1234:56\"}]}]}")]
    [InlineData("{\"format\": \"infopanel.panel-package/1\", \"sdk\": \"1.0\", \"families\": [{\"id\": \"a\", \"name\": \"A\", \"match\": [{\"usb\": \"1234:5678\", \"transport\": \"bluetooth\"}]}]}")]
    [InlineData("{\"format\": \"infopanel.panel-package/1\", \"sdk\": \"1.0\", \"families\": [{\"id\": \"a\", \"name\": \"A\", \"match\": [{\"usb\": \"1234:5678\"}], \"models\": [{\"id\": \"m\", \"name\": \"M\", \"width\": 240}]}]}")]
    [InlineData("{\"format\": \"infopanel.panel-package/1\", \"sdk\": \"1.0\", \"families\": [{\"id\": \"a\", \"name\": \"A\", \"match\": [{\"usb\": \"1234:5678\"}], \"models\": [{\"id\": \"m\", \"name\": \"M\", \"width\": 1, \"height\": 1}, {\"id\": \"m\", \"name\": \"N\", \"width\": 1, \"height\": 1}]}]}")]
    [InlineData("not json")]
    public void A_manifest_the_engine_would_refuse_is_refused(string json)
    {
        Assert.Throws<InvalidDataException>(() => PanelManifest.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_family_the_manifest_doesnt_declare_is_an_error()
    {
        var manifest = PanelManifest.Read(VirtualPanel);

        Assert.Throws<KeyNotFoundException>(() => manifest.Family("turing"));
    }

    // Finding 4: the file is read as the engine reads it (a regular file, not a link, at most MaxBytes, never waiting).

    [Fact]
    public void A_link_to_a_valid_manifest_is_refused_as_the_engine_refuses_it()
    {
        using var temp = new InfoPanel.Core.Tests.Persistence.TempDirectory();
        var target = temp.Combine("real.json");
        File.Copy(VirtualPanel, target);
        var link = temp.Combine("panel.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Windows: making a link needs Developer Mode or the privilege to make one, which this user hasn't.");
        }

        PanelManifest.Read(target); // the file itself reads
        var error = Assert.Throws<InvalidDataException>(() => PanelManifest.Read(link));
        Assert.Contains(link, error.Message);
    }

    [Fact]
    public void A_fifo_is_refused_without_waiting_on_it()
    {
        if (!Fifo.Supported)
            Assert.Skip("Windows: a folder can't hold a FIFO.");
        using var temp = new InfoPanel.Core.Tests.Persistence.TempDirectory();
        var fifo = temp.Combine("panel.json");
        Fifo.Create(fifo);

        Fifo.Within(10, () => Assert.Throws<InvalidDataException>(() => PanelManifest.Read(fifo)), fifo);
    }

    // Round 2, finding 1: a system that can't tell the open file's type (no statx: musl before 1.2.5) refuses it.

    [Fact]
    public void A_file_whose_type_cant_be_told_is_refused()
    {
        ManifestFile.CheckPosixType(ManifestFile.Posix.S_IFREG, "panel.json"); // a regular file reads

        Assert.Throws<IOException>(() => ManifestFile.CheckPosixType(ManifestFile.Posix.S_IFIFO, "panel.json"));
        var error = Assert.Throws<IOException>(() => ManifestFile.CheckPosixType(null, "panel.json"));
        Assert.Contains("panel.json", error.Message);
    }

    // Round 2, finding 2: on Windows the open itself doesn't follow a link, so one put there after the check is refused
    // on the handle read from.

    [Fact]
    public void On_windows_a_link_on_the_opened_handle_is_refused_and_other_reparse_points_read()
    {
        const FileAttributes Link = FileAttributes.Archive | FileAttributes.ReparsePoint;
        var disk = ManifestFile.FILE_TYPE_DISK;

        Assert.Throws<IOException>(() => ManifestFile.CheckWindowsHandle(Link, ManifestFile.IO_REPARSE_TAG_SYMLINK, disk, "panel.json"));
        Assert.Throws<IOException>(() => ManifestFile.CheckWindowsHandle(Link, ManifestFile.IO_REPARSE_TAG_MOUNT_POINT, disk, "panel.json"));
        ManifestFile.CheckWindowsHandle(FileAttributes.Archive, 0, disk, "panel.json");
        ManifestFile.CheckWindowsHandle(Link, 0x9000001A, disk, "panel.json"); // a cloud file's placeholder (OneDrive): not a link
    }

    [Fact]
    public void On_windows_the_open_itself_doesnt_follow_a_link()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Windows only: macOS and Linux open with O_NOFOLLOW, which refuses a link on the open itself (A_link_… covers it).");
        using var temp = new InfoPanel.Core.Tests.Persistence.TempDirectory();
        var target = temp.Combine("real.json");
        File.Copy(VirtualPanel, target);
        var link = temp.Combine("panel.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Windows: making a link needs Developer Mode or the privilege to make one, which this user hasn't.");
        }

        ManifestFile.OpenWindows(target).Dispose();
        Assert.Throws<IOException>(() => ManifestFile.OpenWindows(link).Dispose()); // past the link check, as after a swap
    }

    [Fact]
    public void A_driver_without_a_manifest_beside_it_is_told_where_it_was_looked_for()
    {
        var error = Assert.Throws<InvalidDataException>(() => PanelManifest.Read(typeof(PanelManifestTests).Assembly));

        Assert.Contains(Path.Combine(AppContext.BaseDirectory, "panel.json"), error.Message);
    }
}
