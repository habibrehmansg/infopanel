// V1 oracle, test-only: the protocol code of InfoPanel 1.4.x c71f0f00, InfoPanel/JonsboPanel/JonsboSerialDevice.cs,
// copied verbatim from the lines cited above each part, but for these changes: log lines are left out; GetIdentity's
// port is the test's: the handshake written is a capture into Writes and the reply read is the bytes given to Parse
// (1.4.x read them from the port, the first byte within ReadTimeout and the rest for 300 ms after it, at most 64);
// a reply that never came is a null identity, as 1.4.x's TimeoutException gave. No device code, nothing from Windows.
using System.Text;
using System.Text.RegularExpressions;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle
{
    public sealed partial class V1JonsboSerialDevice
    {
        // :26-30
        public static readonly byte[] HandshakeCommand = [0xF0, 0xA5, 0x5A, 0x0F];

        public const int BaudRate = 9600; // nominal only — CDC bulk ignores it (matches OEM app)
        public const int ReadTimeoutMs = 2000;
        public const int WriteTimeoutMs = 5000;

        // :38-45
        public class DeviceIdentity
        {
            public string Model { get; set; } = "";   // 7-char prefix, e.g. "VMAXB16"
            public string Serial { get; set; } = "";  // e.g. "S261301227"
            public int Width { get; set; }            // native portrait width  (462 on DS916)
            public int Height { get; set; }           // native portrait height (1920 on DS916)
            public string Raw { get; set; } = "";
        }

        /// <summary>The handshakes written, in order.</summary>
        public readonly List<byte[]> Writes = new();

        // :80-136, the part after the read: the reply's first 64 bytes at most, as the read loop kept them
        public DeviceIdentity? GetIdentity(byte[]? replyBytes)
        {
            Writes.Add(HandshakeCommand.ToArray());

            if (replyBytes == null || replyBytes.Length == 0)
                return null; // no first byte within ReadTimeout: TimeoutException

            var buffer = new byte[64];
            int total = Math.Min(replyBytes.Length, buffer.Length);
            Array.Copy(replyBytes, buffer, total);

            string raw = Encoding.ASCII.GetString(buffer, 0, total).Trim('\0', '\r', '\n', ' ');

            // "VMAXB16" + "0462*1920" + "S261301227"
            var match = IdentityRegex().Match(raw);
            if (!match.Success)
            {
                return null;
            }

            return new DeviceIdentity
            {
                Model = match.Groups[1].Value,
                Width = int.Parse(match.Groups[2].Value),
                Height = int.Parse(match.Groups[3].Value),
                Serial = match.Groups[4].Value,
                Raw = raw,
            };
        }

        // :172-173
        [GeneratedRegex(@"^(.{7})(\d{3,4})\*(\d{3,4})(\S*)")]
        private static partial Regex IdentityRegex();
    }
}
