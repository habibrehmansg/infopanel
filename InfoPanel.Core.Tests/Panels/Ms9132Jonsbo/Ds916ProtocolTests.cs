using System.Text;
using InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle;
using InfoPanel.Panels.Ms9132Jonsbo;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The Jonsbo DS916's serial protocol against 1.4.x's own code (<c>V1Oracle/JonsboSerialDevice.cs</c>, 1.4.x c71f0f00):
/// the handshake, the port's settings and timeouts, and the identity parser on the reply 1.4.x documents
/// (<c>VMAXB160462*1920S261301227</c>, JonsboSerialDevice.cs:15-17) and its variants; and the JPEG fit 1.4.x applied
/// (JonsboPanelModelDatabase.cs:39, JonsboPanelDeviceTask.cs:239-249, Models/JonsboPanelDevice.cs:48).
/// </summary>
public sealed class Ds916ProtocolTests
{
    [Fact]
    public void TheHandshakeAndThePort_Are1_4s()
    {
        Assert.Equal(V1JonsboSerialDevice.HandshakeCommand, Ds916Protocol.Handshake.ToArray());
        Assert.Equal((V1JonsboSerialDevice.BaudRate, V1JonsboSerialDevice.ReadTimeoutMs, V1JonsboSerialDevice.WriteTimeoutMs),
            (Ds916Protocol.Baud, Ds916Protocol.ReadTimeoutMs, Ds916Protocol.WriteTimeoutMs));
        Assert.Equal((300, 64), (Ds916Protocol.GatherMs, Ds916Protocol.ReplyCap)); // JonsboSerialDevice.cs:89,97
        Assert.Equal(TimeSpan.FromSeconds(1), Ds916Protocol.RetryGap); // JonsboPanelDeviceTask.cs:71
    }

    [Fact]
    public void TheJpegFit_Is1_4s()
    {
        // 1.4.x's cap (256 KiB), its per-device quality default (80), 15 lower each try down to 30.
        Assert.Equal(JonsboPanelModelDatabase.Models[JonsboPanelModel.DS916].MaxJpegBytes, Ds916Protocol.MaxJpegBytes);
        Assert.Equal((80, 15, 30), (Ds916Protocol.JpegQuality, Ds916Protocol.JpegQualityStep, Ds916Protocol.MinJpegQuality));
    }

    public static TheoryData<string> Replies() =>
    [
        "VMAXB160462*1920S261301227",
        "VMAXB160462*1920S261301227\0\0\r\n",
        "  VMAXB160462*1920S261301227 ",
        "VMAXB16462*1920S1",
        "VMAXB161920*0462",
        "VMAXB160462*1920S26 1301227",
        "VMAXB160462x1920S261301227",
        "VMAXB16*1920",
        "hello",
        "\0\0\0",
        "VMAXB160462*1920" + new string('S', 60),
        "ABCDEFG12345*6789Z",
    ];

    [Theory]
    [MemberData(nameof(Replies))]
    public void TheIdentity_IsReadAs1_4ReadIt(string reply)
    {
        // JonsboSerialDevice.cs:117-135: the reply's first 64 bytes as ASCII, trimmed, then the pattern.
        var bytes = Encoding.ASCII.GetBytes(reply);
        var oracle = new V1JonsboSerialDevice().GetIdentity(bytes);
        var port = Ds916Protocol.Parse(bytes);

        Assert.Equal(oracle == null, port == null);
        if (oracle == null)
            return;
        Assert.Equal((oracle.Model, oracle.Width, oracle.Height, oracle.Serial, oracle.Raw), (port!.Model, port.Width, port.Height, port.Serial, port.Raw));
    }

    [Fact]
    public void TheDocumentedReply_GivesTheDs916sSizeAndSerial()
    {
        var identity = Ds916Protocol.Parse("VMAXB160462*1920S261301227"u8);
        Assert.Equal(("VMAXB16", 462, 1920, "S261301227"), (identity!.Model, identity.Width, identity.Height, identity.Serial));
        Assert.Null(Ds916Protocol.Parse([]));
    }
}
