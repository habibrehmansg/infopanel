using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// The Jonsbo DS916's serial protocol, InfoPanel 1.4's (JonsboSerialDevice.cs, from a capture of the vendor's app): the
/// handshake <c>F0 A5 5A 0F</c>, answered by an ASCII identity with the model code, the native size and the serial; then
/// one raw JPEG a frame, with nothing around it, and no command to stop.
/// </summary>
internal static partial class Ds916Protocol
{
    /// <summary>The handshake (JonsboSerialDevice.cs:26).</summary>
    public static ReadOnlySpan<byte> Handshake => [0xF0, 0xA5, 0x5A, 0x0F];

    /// <summary>The nominal rate, which the CDC firmware ignores (JonsboSerialDevice.cs:28); DTR and RTS raised (:57-58).</summary>
    public const int Baud = 9600;

    /// <summary>The wait for the reply's first byte (JonsboSerialDevice.cs:29,93).</summary>
    public const int ReadTimeoutMs = 2000;

    /// <summary>A write's timeout (JonsboSerialDevice.cs:30).</summary>
    public const int WriteTimeoutMs = 5000;

    /// <summary>How long the rest of the reply is gathered after its first byte (JonsboSerialDevice.cs:97).</summary>
    public const int GatherMs = 300;

    /// <summary>The most bytes of a reply kept (JonsboSerialDevice.cs:89).</summary>
    public const int ReplyCap = 64;

    /// <summary>The pause before the handshake is asked again after silence (JonsboPanelDeviceTask.cs:69-73).</summary>
    public static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(1);

    /// <summary>The JPEG cap, 1.4's soft one (JonsboPanelModelDatabase.cs:39).</summary>
    public const int MaxJpegBytes = 256 * 1024;

    /// <summary>The JPEG quality, 1.4's per-device default (Models/JonsboPanelDevice.cs:48).</summary>
    public const int JpegQuality = 80;

    /// <summary>1.4's fit: 15 lower each try, down to 30 (JonsboPanelDeviceTask.cs:243-249).</summary>
    public const int JpegQualityStep = 15;

    /// <inheritdoc cref="JpegQualityStep"/>
    public const int MinJpegQuality = 30;

    [GeneratedRegex(@"^(.{7})(\d{3,4})\*(\d{3,4})(\S*)")]
    private static partial Regex IdentityPattern();

    /// <summary>
    /// The identity a reply gives, as 1.4 read it (JonsboSerialDevice.cs:117-135): its first <see cref="ReplyCap"/> bytes
    /// as ASCII, trimmed of NULs, line ends and spaces, then a 7-character model code, the width, <c>*</c>, the height and
    /// the serial; null when it doesn't read so.
    /// </summary>
    public static Ds916Identity? Parse(ReadOnlySpan<byte> reply)
    {
        if (reply.IsEmpty)
            return null;
        var raw = Encoding.ASCII.GetString(reply[..Math.Min(reply.Length, ReplyCap)]).Trim('\0', '\r', '\n', ' ');
        var match = IdentityPattern().Match(raw);
        if (!match.Success)
            return null;
        return new Ds916Identity(match.Groups[1].Value,
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            match.Groups[4].Value, raw);
    }
}

/// <summary>A DS916's identity: its model code (<c>VMAXB16</c>), its native size, its serial and the reply as read.</summary>
internal sealed record Ds916Identity(string Model, int Width, int Height, string Serial, string Raw);
