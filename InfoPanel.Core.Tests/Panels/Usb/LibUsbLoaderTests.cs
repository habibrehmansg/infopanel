using Xunit;
using System.Runtime.InteropServices;
using InfoPanel.Panels.Usb;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace InfoPanel.Core.Tests.Panels.Usb;

/// <summary>Where libusb-1.0 is looked for, and what happens when it isn't there.</summary>
public class LibUsbLoaderTests
{
    private static readonly string Engine = Path.Combine(Path.GetTempPath(), "engine");
    private static readonly string Named = Path.Combine(Path.GetTempPath(), "named");

    [Fact]
    public void On_macOS_beside_the_engine_then_the_named_folder_then_Homebrews()
    {
        Assert.Equal(
        [
            Path.Combine(Engine, "libusb-1.0.dylib"),
            Path.Combine(Named, "libusb-1.0.dylib"),
            Path.Combine("/opt/homebrew/lib", "libusb-1.0.dylib"),
            Path.Combine("/usr/local/lib", "libusb-1.0.dylib"),
        ], LibUsb.Candidates(Engine, Named, OSPlatform.OSX));
    }

    [Fact]
    public void On_Windows_beside_the_engine_then_the_named_folder()
    {
        Assert.Equal([Path.Combine(Engine, "libusb-1.0.dll"), Path.Combine(Named, "libusb-1.0.dll")],
            LibUsb.Candidates(Engine, Named, OSPlatform.Windows));
    }

    [Fact]
    public void On_Linux_beside_the_engine_then_the_named_folder_then_the_systems()
    {
        Assert.Equal([Path.Combine(Engine, "libusb-1.0.so.0"), Path.Combine(Named, "libusb-1.0.so.0"), "libusb-1.0.so.0"],
            LibUsb.Candidates(Engine, Named, OSPlatform.Linux));
    }

    [Fact]
    public void Without_the_variable_its_folder_is_skipped()
    {
        Assert.Equal([Path.Combine(Engine, "libusb-1.0.dll")], LibUsb.Candidates(Engine, null, OSPlatform.Windows));
        Assert.Equal([Path.Combine(Engine, "libusb-1.0.dll")], LibUsb.Candidates(Engine, "", OSPlatform.Windows));
    }

    [Fact]
    public void The_default_candidates_start_beside_the_engine()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, LibUsb.FileName(OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux)), LibUsb.DefaultCandidates()[0]);
    }

    [Fact]
    public void With_no_library_found_panels_are_unsupported_with_one_log_line()
    {
        var folder = Directory.CreateTempSubdirectory("libusb-missing-").FullName;
        var notALibrary = Path.Combine(folder, "libusb-1.0.dylib");
        File.WriteAllText(notALibrary, "not a library");
        string[] candidates = [Path.Combine(folder, "one", "libusb-1.0.dylib"), notALibrary, Path.Combine(folder, "two", "libusb-1.0.dylib")];

        var sink = new Collecting();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var library = LibUsb.LoadAndLog(candidates, logger);

        Assert.Null(library);
        var line = Assert.Single(sink.Events);
        var text = line.RenderMessage();
        Assert.Contains("USB panels are unsupported", text);
        // Every place, in order, with why it was passed over.
        var first = text.IndexOf(candidates[0], StringComparison.Ordinal);
        var second = text.IndexOf(notALibrary + ": can't be loaded", StringComparison.Ordinal);
        var third = text.IndexOf(candidates[2], StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first && third > second, text);
    }

    [Fact]
    public void Load_answers_why_without_throwing()
    {
        Assert.Null(LibUsb.Load([], out var why));
        Assert.NotEmpty(why);
        Assert.Null(LibUsb.Load([Path.Combine(Engine, "nowhere", "libusb-1.0.dylib")], out why));
        Assert.Contains("not found", why);
    }

    [Fact]
    public void Homebrews_libusb_loads_where_it_is_installed()
    {
        var path = "/opt/homebrew/lib/libusb-1.0.dylib";
        Assert.SkipUnless(OperatingSystem.IsMacOS() && File.Exists(path), "libusb isn't installed by Homebrew here.");
        var library = LibUsb.Load([Path.Combine(Engine, "libusb-1.0.dylib"), path], out var why);
        Assert.True(library is not null, why);
        Assert.Equal(path, library.Path);
        Assert.StartsWith("1.0.", library.Version);
    }

    private sealed class Collecting : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
