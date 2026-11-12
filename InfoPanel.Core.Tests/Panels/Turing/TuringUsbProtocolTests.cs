using InfoPanel.Core.Tests.Panels.Turing.V1Oracle;
using InfoPanel.Panels.Turing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// The USB protocol's bytes (§2.2 (1)) against the code 1.4.x ran: Lian Li's commands against 1.4.x's
/// <c>LianLiPanel/LianLiUsbScreenDevice.cs</c> (<c>V1Oracle/LianLiUsbScreenDevice.cs</c>), Turing's against the
/// library's <c>ScreenDevice</c>, the source of the <c>Lib/LcdDriver.TuringSmartScreen.dll</c> 1.4.x drove Turing's USB
/// panels with (<c>Services/TuringPanelUsbDeviceTask.cs:4,119</c>; <c>V1Oracle/ScreenDevice.cs</c>), and against 1.4.x's
/// own unused <c>TuringPanel/TuringDevice.cs</c> (<c>V1Oracle/TuringDevice.cs</c>); each fed the same timestamp.
/// </summary>
public sealed class TuringUsbProtocolTests
{
    private const int Timestamp = 0x0123_4567;

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public static TheoryData<byte, byte?, int?> Commands() => new()
    {
        { 10, null, null },        // Sync / GetVersion
        { 14, 70, null },          // brightness 70
        { 14, 0, null },           // brightness 0, the stop's
        { 15, 30, null },          // Lian Li's frame rate
        { 52, 0, null },           // Lian Li's StopClock
        { 101, null, 123_456 },    // a JPEG's command, its length big-endian
        { 102, null, 4321 },       // a PNG's
        { 111, null, null },       // Turing's StopMedia
        { 122, null, null },       // Lian Li's QueryBlock
        { 123, null, null },       // Lian Li's StopMedia
    };

    private static byte[] Port(byte id, byte? value, int? length) =>
        value is { } v ? TuringUsbProtocol.WithValue(id, v, Timestamp)
        : length is { } l ? TuringUsbProtocol.WithLength(id, l, Timestamp)
        : TuringUsbProtocol.Command(id, Timestamp);

    [Theory]
    [MemberData(nameof(Commands))]
    public void TheCommand_IsLianLisAndTheLibrarys(byte id, byte? value, int? length)
    {
        // LianLiUsbScreenDevice.cs:205-213 (byte 0 the command, bytes 2-3 26 109, 4-7 the timestamp little-endian), with
        // :77, :134, :103 (byte 8) and :186 (bytes 8-11, the length big-endian); ScreenDevice's PrepareCommandHeader the same.
        var lianLi = new V1LianLiUsbScreenDevice(new OracleUsb()) { Timestamp = Timestamp };
        var library = new V1ScreenDevice(new OracleUsb()) { Timestamp = Timestamp };

        var command = Port(id, value, length);

        Assert.Equal(500, command.Length);
        Assert.Equal(Hex(lianLi.CommandFor(id, value, length)), Hex(command));
        Assert.Equal(Hex(library.CommandFor(id, value, length)), Hex(command));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void LianLisPacket_IsLianLis(byte id, byte? value, int? length)
    {
        // LianLiUsbScreenDevice.cs:215-223: DES-CBC, key and IV "slv3tuzx" (:26), PKCS7 over the 500 bytes, then 161 26.
        using var lianLi = new V1LianLiUsbScreenDevice(new OracleUsb()) { Timestamp = Timestamp };
        lianLi.CommandFor(id, value, length);

        var packet = TuringUsbProtocol.Packet(UsbVariant.LianLi, Port(id, value, length));

        Assert.Equal(Hex(lianLi.Packet()), Hex(packet));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void TuringsPacket_IsTheLibrarys(byte id, byte? value, int? length)
    {
        // ScreenDevice.RequestResponse: DES-CBC, key and IV "slv3tuzx", no padding over the command zero-padded to 504
        // bytes, then A1 1A.
        using var library = new V1ScreenDevice(new OracleUsb()) { Timestamp = Timestamp };
        library.CommandFor(id, value, length);

        var packet = TuringUsbProtocol.Packet(UsbVariant.Turing, Port(id, value, length));

        Assert.Equal(Hex(library.Packet()), Hex(packet));
    }

    [Fact]
    public void TuringDevices_10_14_And_101_AreTheLibrarys_AndDisagreeWithLianLisInTheLastCipherBlock()
    {
        // The brief's check: TuringDevice.cs's command bytes for 10, 14 and 101 against Lian Li's. They agree with the
        // library's ScreenDevice (what 1.4.x ran for Turing USB) byte for byte, and with Lian Li's in the header and all
        // but one block: TuringDevice and the library pad the 500 bytes with zeros (:163-166), Lian Li with PKCS7
        // (:217), so the packets' bytes 496-503 (the cipher's last block) differ. So Turing USB keeps the library's
        // padding and Lian Li its own.
        var turingDevice = new V1TuringDevice { Timestamp = Timestamp };
        using var lianLi = new V1LianLiUsbScreenDevice(new OracleUsb()) { Timestamp = Timestamp };
        using var library = new V1ScreenDevice(new OracleUsb()) { Timestamp = Timestamp };

        foreach (var (id, value, length, mine) in new (byte, byte?, int?, byte[])[]
        {
            (10, null, null, turingDevice.Sync()),
            (14, 70, null, turingDevice.Brightness(70)),
            (101, null, 123_456, turingDevice.Jpeg(123_456)),
        })
        {
            lianLi.CommandFor(id, value, length);
            library.CommandFor(id, value, length);
            var theirs = lianLi.Packet();

            Assert.Equal(Hex(library.Packet()), Hex(mine));
            Assert.Equal(Hex(TuringUsbProtocol.Packet(UsbVariant.Turing, Port(id, value, length))), Hex(mine));
            Assert.Equal(Hex(theirs[..496]), Hex(mine[..496]));
            Assert.Equal(Hex(theirs[504..]), Hex(mine[504..]));
            Assert.NotEqual(Hex(theirs[496..504]), Hex(mine[496..504]));
        }
    }

    [Fact]
    public void StopMedia_Is111ForTuring_And123ForLianLi()
    {
        // ScreenDevice.StopMedia: 111; LianLiUsbScreenDevice.cs:42,81: 123.
        Assert.Equal(111, TuringUsbProtocol.StopMedia(UsbVariant.Turing));
        Assert.Equal(123, TuringUsbProtocol.StopMedia(UsbVariant.LianLi));
    }

    [Fact]
    public void TheClockCommand_IsLianLis()
    {
        // LianLiUsbScreenDevice.cs:83-98: the year big-endian, month, day, hour, minute, second, then 2.
        var now = new DateTime(2026, 11, 12, 15, 4, 59, DateTimeKind.Local);

        var packet = TuringUsbProtocol.Packet(UsbVariant.LianLi, TuringUsbProtocol.Clock(now, Timestamp));

        var oracle = new OracleUsb();
        using var recorded = new V1LianLiUsbScreenDevice(oracle) { Timestamp = Timestamp, Now = now };
        recorded.SyncClockOnly();
        Assert.Equal(Hex(oracle.Writes[0]), Hex(packet));
    }

    [Fact]
    public void AnImageOverLianLisLimit_IsNeverSent()
    {
        // LianLiUsbScreenDevice.cs:33,176-183: an image over 512,000 bytes is dropped, nothing written.
        var oracle = new OracleUsb();
        using var lianLi = new V1LianLiUsbScreenDevice(oracle);

        Assert.False(lianLi.DrawJpeg(new byte[TuringUsbProtocol.MaxImagePayload + 1]));
        Assert.Empty(oracle.Writes);
        Assert.Equal(512_000, TuringUsbProtocol.MaxImagePayload);
    }
}
