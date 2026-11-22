using InfoPanel.Api;
using InfoPanel.Hosting;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The release lines' codenames (<see cref="ReleaseCodename"/>): the list read by the rule the release workflow checks it
/// with (comments, empty lines, every refusal naming its line), a version's line by its major.minor, the shipped list,
/// and a list that can't be read or breaks the rule giving no name and one warning, never an exception.
/// </summary>
public sealed class ReleaseCodenameTests
{
    private sealed class Collecting(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    private readonly List<LogEvent> _log = [];

    private Serilog.ILogger Logger() =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Collecting(_log)).CreateLogger();

    private static Stream Text(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void TheList_SkipsCommentsAndEmptyLines_AndKeepsEachLinesName()
    {
        var entries = ReleaseCodename.Parse("# The codenames.\n# key=value\n\n2.0=Road Runner\n\n2.1=Alpha-2\n10.0=X\n");

        Assert.Equal(new Dictionary<string, string> { ["2.0"] = "Road Runner", ["2.1"] = "Alpha-2", ["10.0"] = "X" }, entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("# only a comment")]
    [InlineData("#2.0=\"not read\"\n")]
    public void AListWithNoEntry_NamesNoLine(string text) => Assert.Empty(ReleaseCodename.Parse(text));

    [Fact]
    public void TheLastLine_NeedsNoLineEnd_AndANameMayHave32Characters()
    {
        var name = "A" + new string('b', 31);

        Assert.Equal(name, ReleaseCodename.Parse("2.0=" + name)["2.0"]);
    }

    [Theory]
    [InlineData("2.0=\"Evan\"\n", 1)]
    [InlineData("2.0=$(id)\n", 1)]
    [InlineData("2.0=Evan \n", 1)]
    [InlineData("2.0= Evan\n", 1)]
    [InlineData("2.0=A  B\n", 1)]
    [InlineData("2.0=A--B\n", 1)]
    [InlineData("2.0=A-\n", 1)]
    [InlineData("2.0=9Lives\n", 1)]
    [InlineData("2.0=Zoë\n", 1)]
    [InlineData("2.0=Abcdefghijklmnopqrstuvwxyzabcdefg\n", 1)]
    [InlineData("2.0=A<b>\n", 1)]
    [InlineData("2.0=A:B\n", 1)]
    [InlineData("# fine\n2.0=Evan\r\n", 2)]
    [InlineData("2.0=Evan\t\n", 1)]
    [InlineData("# a tab\there\n", 1)]
    [InlineData("2.0=Ev\u007fan\n", 1)]
    [InlineData("2.0=\n", 1)]
    [InlineData("=Evan\n", 1)]
    [InlineData("02.0=X\n", 1)]
    [InlineData("2.00=X\n", 1)]
    [InlineData("2=X\n", 1)]
    [InlineData("2.0.0=X\n", 1)]
    [InlineData("v2.0=X\n", 1)]
    [InlineData("2.0 =X\n", 1)]
    [InlineData("2.0 Evan\n", 1)]
    [InlineData(" # an indented comment\n", 1)]
    [InlineData("﻿# a byte order mark\n", 1)]
    [InlineData("# fine\n\n2.0=A\n2.0=B\n", 4)]
    [InlineData("2.0=A\n2.1=B=C\n", 2)]
    public void ALineTheRuleRefuses_RefusesTheList_NamingTheLine(string text, int line)
    {
        var refused = Assert.Throws<FormatException>(() => ReleaseCodename.Parse(text));

        Assert.Contains($"line {line}", refused.Message);
    }

    [Fact]
    public void ANulByte_RefusesTheList() => Assert.Throws<FormatException>(() => ReleaseCodename.Parse("# a\0b\n2.0=Evan\n"));

    [Fact]
    public void ARefusal_NeverRepeatsTheName()
    {
        var refused = Assert.Throws<FormatException>(() => ReleaseCodename.Parse("2.0=Secret Name!\n"));

        Assert.DoesNotContain("Secret", refused.Message);
    }

    [Theory]
    [InlineData("2.1.0", "One")]
    [InlineData("2.1.10", "One")]
    [InlineData("2.10.0", "Ten")]
    [InlineData("2.10.1-beta.2", "Ten")]
    [InlineData("2.100.0", null)]
    [InlineData("12.1.0", null)]
    public void AVersion_TakesItsOwnLinesName_2Point1IsNot2Point10(string version, string? expected) =>
        Assert.Equal(expected, ReleaseCodename.Of(version, ReleaseCodename.Parse("2.1=One\n2.10=Ten\n")));

    // A list the test builds, so what a line has no name for never depends on the lines the shipped list holds.
    private static readonly IReadOnlyDictionary<string, string> Fixture = ReleaseCodename.Parse("2.0=Evan\n");

    [Theory]
    [InlineData("2.0.0-beta.1", "Evan")]
    [InlineData("2.0.0", "Evan")]
    [InlineData("2.0.3", "Evan")]
    [InlineData("2.0.0-dev", "Evan")]
    [InlineData("2.0.0-dev.ci.18", "Evan")]
    [InlineData("2.1.0", null)]
    [InlineData("3.0.0", null)]
    [InlineData("2.0", null)]
    [InlineData("v2.0.0", null)]
    [InlineData("02.0.0", null)]
    [InlineData("2.00.0", null)]
    [InlineData("2.0.0-", null)]
    [InlineData("2.0.0-beta..1", null)]
    [InlineData("", null)]
    public void AVersion_OnALineWithAName_GetsIt_AndOnAnyOtherLineOrNonVersionNone(string version, string? expected) =>
        Assert.Equal(expected, ReleaseCodename.Of(version, Fixture));

    // Only what stays true as the shipped list grows: it parses whole, and its 2.0 line is Evan.
    [Theory]
    [InlineData("2.0.0-beta.1")]
    [InlineData("2.0.0")]
    [InlineData("2.0.3")]
    [InlineData("2.0.0-dev")]
    public void TheShippedList_ParsesWhole_AndNames2Point0Evan(string version)
    {
        using var shipped = typeof(ReadEndpoints).Assembly.GetManifestResourceStream(ReleaseCodename.Resource);
        Assert.NotNull(shipped);
        using var reader = new StreamReader(shipped);

        Assert.Equal("Evan", ReleaseCodename.Of(version, ReleaseCodename.Parse(reader.ReadToEnd())));
    }

    [Fact]
    public void TheEngine_IsOn2Point0_AndNamesItsLine()
    {
        // Every test engine and source build is 2.0.0-dev, on the 2.0 line.
        Assert.StartsWith("2.0.", ReadEndpoints.EngineVersion());
        Assert.Equal("Evan", ReadEndpoints.EngineCodename());
    }

    [Fact]
    public void AListThatReads_GivesItsNames_AndLogsNothing()
    {
        var entries = ReleaseCodename.Load(() => Text("# c\n2.0=Evan\n"), Logger());

        Assert.Equal("Evan", ReleaseCodename.Of("2.0.0-beta.1", entries));
        Assert.Null(ReleaseCodename.Of("2.1.0", entries));
        Assert.Empty(_log);
    }

    [Fact]
    public void TheEngine_NamesAVersionOfTheShippedLists2Point0Line_AndNoNonVersion()
    {
        Assert.Equal("Evan", ReadEndpoints.CodenameOf("2.0.0-beta.1"));
        Assert.Null(ReadEndpoints.CodenameOf("v2.0.0"));
    }

    public static TheoryData<string> Unreadable => ["missing", "open fails", "read fails", "invalid", "duplicate"];

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void AListThatCantBeRead_OrBreaksTheRule_GivesNoName_AndOneWarning_NeverAnException(string kind)
    {
        Func<Stream?> open = kind switch
        {
            "missing" => () => null,
            "open fails" => () => throw new IOException("no list"),
            "read fails" => () => new FailingStream(),
            "invalid" => () => Text("2.0=\"Evan\"\n"),
            _ => () => Text("2.0=Evan\n2.0=Evan\n"),
        };

        var entries = ReleaseCodename.Load(open, Logger());

        Assert.Empty(entries);
        Assert.Null(ReleaseCodename.Of("2.0.0", entries));
        var warning = Assert.Single(_log);
        Assert.Equal(LogEventLevel.Warning, warning.Level);
    }

    [Fact]
    public void TheVersionLine_IsTheVersion_ThenTheNameInTypographicQuotes_OrTheVersionAlone()
    {
        Assert.Equal("2.0.0-beta.2 “Evan”", ReleaseCodename.VersionLine("2.0.0-beta.2", "Evan"));
        Assert.Equal("2.1.0", ReleaseCodename.VersionLine("2.1.0", null));
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("the read failed");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
