using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// The kit's text trace (<see cref="PanelTrace"/>): one line a call, which a family's tests write by hand from 1.x's call
/// order and a tester's hardware run records; formatting and parsing are each other's inverse.
/// </summary>
public sealed class PanelTraceTests
{
    // Every kind of line, a status on some, a zero-length packet, and a device's comment before an open.
    private const string EveryKind =
        "# 1234:5678 at 1-2 (bus 1, address 7)\n" +
        "h1 open usb if0\n" +
        "h1 > ep01 0a00ff\n" +
        "h1 > ep01 zlp\n" +
        "h1 < ep81 0102\n" +
        "h1 < ep82 !timeout\n" +
        "h1 > ep01 00 !gone\n" +
        "h1 close usb if0\n" +
        "h2 open usb if3 !busy\n" +
        "h3 open hid hid0\n" +
        "h3 hid> 0010\n" +
        "h3 hid< 0042 !io\n" +
        "h3 hid-feature> 0507\n" +
        "h3 hid-feature< 050102\n" +
        "h3 close hid\n" +
        "h4 open serial 2000000 dtr rts\n" +
        "h4 serial> 48454c4c4f\n" +
        "h4 serial< 4f4b\n" +
        "h4 serial discard !access\n" +
        "h4 close serial\n" +
        "h5 open serial 115200 rts !notSupported\n" +
        "h6 open hid !notSupported\n";

    // Two panels, the same endpoint, their writes interleaved.
    private const string TwoPanels =
        "# 1234:5678 at 1-2 (bus 1, address 7)\n" +
        "h1 open usb if0\n" +
        "# 1234:5678 at 1-3 (bus 1, address 9)\n" +
        "h2 open usb if0\n" +
        "h1 > ep01 aa\n" +
        "h2 > ep01 bb\n" +
        "h1 > ep01 a2\n" +
        "h2 close usb if0\n" +
        "h1 close usb if0\n";

    [Fact]
    public void A_two_panel_trace_tells_each_write_by_its_handle_and_each_handle_by_its_device()
    {
        var entries = PanelTrace.Parse(TwoPanels);

        var devices = entries.Where(e => e.Op == PanelTraceOp.Open).ToDictionary(e => e.Handle, e => e.Device);
        Assert.Equal("1234:5678 at 1-2 (bus 1, address 7)", devices[1]);
        Assert.Equal("1234:5678 at 1-3 (bus 1, address 9)", devices[2]);
        var writes = entries.Where(e => e.Op == PanelTraceOp.UsbWrite).Select(e => (devices[e.Handle], e.Data[0])).ToArray();
        Assert.Equal([("1234:5678 at 1-2 (bus 1, address 7)", (byte)0xaa), ("1234:5678 at 1-3 (bus 1, address 9)", (byte)0xbb), ("1234:5678 at 1-2 (bus 1, address 7)", (byte)0xa2)], writes);
        Assert.Equal([2, 1], entries.Where(e => e.Op == PanelTraceOp.Close).Select(e => e.Handle));
        Assert.Null(entries.First(e => e.Op == PanelTraceOp.UsbWrite).Device); // only an open has a device

        var trace = new PanelTrace();
        foreach (var entry in entries)
            trace.Add(entry);
        Assert.Equal(TwoPanels, trace.Format());
        Assert.Throws<FormatException>(() => PanelTrace.Parse("> ep01 00")); // a call names its handle
    }

    [Fact]
    public void Parsing_then_formatting_gives_the_same_text()
    {
        var trace = new PanelTrace();
        foreach (var entry in PanelTrace.Parse(EveryKind))
            trace.Add(entry);

        Assert.Equal(EveryKind, trace.Format());
    }

    [Fact]
    public void Formatting_then_parsing_gives_the_same_entries()
    {
        PanelTraceEntry[] entries =
        [
            new(PanelTraceOp.Open, "usb if0", []),
            new(PanelTraceOp.UsbWrite, "ep01", [0x00, 0xab, 0xff]),
            new(PanelTraceOp.UsbWrite, "ep01", []),
            new(PanelTraceOp.UsbRead, "ep81", [], PanelDeviceStatus.Timeout),
            new(PanelTraceOp.HidSetFeature, "", [0x05]),
            new(PanelTraceOp.SerialWrite, "", [0x01, 0x02], PanelDeviceStatus.Io),
            new(PanelTraceOp.SerialDiscard, "", []),
            new(PanelTraceOp.Close, "serial", []),
        ];
        var trace = new PanelTrace();
        foreach (var entry in entries)
            trace.Add(entry);

        Assert.Equal(entries, PanelTrace.Parse(trace.Format()));
    }

    [Fact]
    public void Comments_and_blank_lines_are_not_calls()
    {
        var entries = PanelTrace.Parse("# 1234:5678 at 1-2\n\nh1 open usb if0\r\n  # the start\nh1 > ep01 01\n");

        Assert.Equal([PanelTraceOp.Open, PanelTraceOp.UsbWrite], entries.Select(e => e.Op));
        Assert.Equal([0x01], entries[1].Data);
    }

    [Theory]
    [InlineData("h1 > ep01 0")] // half a byte
    [InlineData("h1 > ep01 zz")]
    [InlineData("h1 > 01 00")] // no endpoint
    [InlineData("h1 hid> 00 !sleepy")] // no such status
    [InlineData("h1 usb> 00")]
    [InlineData("h1 open floppy")]
    public void A_line_that_isnt_a_call_is_refused(string line)
    {
        Assert.Throws<FormatException>(() => PanelTrace.Parse(line));
    }

    [Fact]
    public void Entries_compare_by_their_bytes()
    {
        Assert.Equal(new PanelTraceEntry(PanelTraceOp.HidWrite, "", [0x01]), new PanelTraceEntry(PanelTraceOp.HidWrite, "", [0x01]));
        Assert.NotEqual(new PanelTraceEntry(PanelTraceOp.HidWrite, "", [0x01]), new PanelTraceEntry(PanelTraceOp.HidWrite, "", [0x02]));
    }
}
