// V1 oracle, test-only: the protocol code of InfoPanel 1.4.x c71f0f00, InfoPanel/JonsboPanel/JonsboMs9132Device.cs,
// copied verbatim from the lines cited above each part, but for these changes: log lines are left out (ParseEdid's two
// log lines are kept as the text they log, in EdidLog); the HID stream's SetFeature is a capture into Reports and its
// GetFeature fills the buffer from GetFeature (the test's device); Thread.Sleep is a capture into Delays (timing, not
// bytes); the bulk writer is a capture into Chunks, which answers each write as the test says (Writer); the writer's
// Reset after a failed write is counted in Resets. Open, Dispose and the HID and libusb lookups are device code and left
// out. No device code, nothing from Windows.
namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle
{
    public sealed class V1JonsboMs9132Device
    {
        // :40-47
        public const int VENDOR_ID = 0x345F;
        public const int PRODUCT_ID = 0x9132;

        private const int ChunkSize = 65536;
        private const int WriteTimeoutMs = 5000;

        /// <summary>Gap the OEM app leaves between A6 04 01 and the first frame.</summary>
        private const int FirstFrameDelayMs = 400;

        // :50-77
        /// <summary>Power-up, register setup and EDID read block (OEM steps 1-56).</summary>
        public static readonly byte[][] StartupReports =
        [
            [0xB5, 0xFF, 0x00, 0, 0, 0, 0, 0],
            [0xB5, 0xF0, 0x00, 0, 0, 0, 0, 0],
            [0xB5, 0x00, 0x31, 0, 0, 0, 0, 0],
            [0xB5, 0x00, 0x30, 0, 0, 0, 0, 0],
            [0xB5, 0xF4, 0x39, 0, 0, 0, 0, 0],
            [0xF5, 0x00, 0x1F, 0xE0, 0, 0, 0, 0],
            [0xF5, 0x00, 0x1F, 0xE8, 0, 0, 0, 0],
            [0xF5, 0x00, 0x1F, 0xF0, 0, 0, 0, 0],
            [0xF5, 0x00, 0x1F, 0xF8, 0, 0, 0, 0],
            [0xA6, 0x07, 0x01, 0x02, 0, 0, 0, 0],   // power on
            [0xB5, 0xC4, 0x54, 0, 0, 0, 0, 0],
            [0xA6, 0x05, 0x00, 0, 0, 0, 0, 0],      // video off
            [0xB5, 0xC5, 0x55, 0, 0, 0, 0, 0],
            [0xB5, 0xF0, 0x05, 0, 0, 0, 0, 0],
            [0xB6, 0xF0, 0x05, 0x20, 0, 0, 0, 0],   // display control register, output still off
            [0xC5, 0xB0, 0x00, 0, 0, 0, 0, 0],
            [0xC6, 0xB0, 0xDF, 0, 0, 0, 0, 0],
            [0xC5, 0xA0, 0x00, 0, 0, 0, 0, 0],
            [0xC6, 0xA0, 0xE4, 0, 0, 0, 0, 0],
            [0xC5, 0xA0, 0x00, 0, 0, 0, 0, 0],
            [0xC6, 0xA0, 0xF0, 0, 0, 0, 0, 0],
            [0xB5, 0xF0, 0x16, 0, 0, 0, 0, 0],
            [0xB6, 0xF0, 0x16, 0x00, 0, 0, 0, 0],
            [0xB5, 0x00, 0x32, 0, 0, 0, 0, 0],      // panel status
            .. Enumerable.Range(0, 32).Select(i => new byte[] { 0xB5, 0xC0, (byte)(i * 4), 0, 0, 0, 0, 0 }),
        ];

        // :80-86
        /// <summary>Output enable, sent once frames are flowing (OEM steps 67-70).</summary>
        public static readonly byte[][] EnableVideoReports =
        [
            [0xB5, 0xF0, 0x05, 0, 0, 0, 0, 0],
            [0xB6, 0xF0, 0x05, 0x30, 0, 0, 0, 0],   // display control, output bit set
            [0xA6, 0x05, 0x01, 0, 0, 0, 0, 0],      // video on
            [0xB5, 0xC5, 0x55, 0, 0, 0, 0, 0],
        ];

        // :88
        public static readonly byte[] PanelStatusReport = [0xB5, 0x00, 0x32, 0, 0, 0, 0, 0];

        // :95-106
        public static readonly Dictionary<(int Width, int Height), byte> VicByResolution = new()
        {
            [(480, 480)] = 143,
            [(240, 320)] = 147,
            [(320, 240)] = 148,
            [(240, 240)] = 156,
            [(480, 272)] = 159,
            [(360, 960)] = 160,
            [(376, 960)] = 171,
            [(320, 960)] = 178,
            [(600, 1600)] = 177,
        };

        // :108-109
        public static byte? GetVicForResolution(int width, int height) =>
            VicByResolution.TryGetValue((width, height), out var vic) ? vic : null;

        private bool _videoEnabled;

        // :120-126
        public byte[]? Edid { get; private set; }

        public (int Width, int Height)? EdidResolution { get; private set; }

        public string? EdidPanelName { get; private set; }

        /// <summary>ParseEdid's two log lines, as the text they log (Serilog's rendering of the values).</summary>
        public readonly List<string> EdidLog = new();

        /// <summary>Every feature report set, in order, as the 9 bytes SetFeature was given.</summary>
        public readonly List<byte[]> Reports = new();

        /// <summary>The test's device: fills GetFeature's buffer (9 bytes, report id 0 first) for the report last set.</summary>
        public Action<byte[], byte[]> GetFeature = (_, _) => { };

        /// <summary>Every Thread.Sleep, in milliseconds, in order.</summary>
        public readonly List<int> Delays = new();

        /// <summary>Every bulk write's bytes, in order.</summary>
        public readonly List<byte[]> Chunks = new();

        /// <summary>Each bulk write's answer, from its index (from 0): whether all its bytes went.</summary>
        public Func<int, bool> Writer = _ => true;

        public int Resets;

        // :211-229
        public void RunStartupBlock()
        {
            var edid = new byte[128];
            bool haveEdid = false;

            foreach (var report in StartupReports)
            {
                var reply = SendReport(report);

                // B5 C0 xx returns the 4 EDID bytes at offset xx; the block reads 0x00..0x7C.
                if (report[0] == 0xB5 && report[1] == 0xC0 && report[2] + 4 <= edid.Length)
                {
                    Array.Copy(reply, 0, edid, report[2], 4);
                    haveEdid = true;
                }
            }

            if (haveEdid) ParseEdid(edid);
        }

        // :231-266
        public void ParseEdid(byte[] edid)
        {
            // Block 0 always starts 00 FF FF FF FF FF FF 00.
            if (edid[1] != 0xFF || edid[2] != 0xFF || edid[6] != 0xFF || edid[7] != 0x00)
            {
                return;
            }

            Edid = edid;

            // Preferred detailed timing descriptor at 0x36: hactive and vactive each split
            // across a low byte and the high nibble/2 bits of a shared byte.
            int hActive = ((edid[0x36 + 4] & 0xF0) << 4) | edid[0x36 + 2];
            int vActive = ((edid[0x36 + 7] & 0x30) << 4) | edid[0x36 + 5];
            if (hActive > 0 && vActive > 0)
                EdidResolution = (hActive, vActive);

            // Descriptors 2-4 (0x48, 0x5A, 0x6C): tag 0xFC is the monitor name.
            for (int offset = 0x48; offset <= 0x6C; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != 0xFC) continue;
                EdidPanelName = System.Text.Encoding.ASCII
                    .GetString(edid, offset + 5, 13)
                    .Split('\n')[0]
                    .Trim();
                break;
            }

            EdidLog.Add(FormattableString.Invariant($"JonsboMs9132: EDID reports {hActive}x{vActive} @ {((edid[0x37] << 8) | edid[0x36]) / 100.0:F2} MHz, panel name {EdidPanelName ?? "(none)"}"));
            EdidLog.Add($"JonsboMs9132: EDID block 0: {Convert.ToHexString(edid)}");
        }

        // :272-287
        public byte[] SendReport(byte[] payload8)
        {
            // Feature report: [reportId=0] + 8 payload bytes.
            var buffer = new byte[9];
            Array.Copy(payload8, 0, buffer, 1, Math.Min(payload8.Length, 8));
            Reports.Add(buffer);

            var reply = new byte[9];
            GetFeature(buffer, reply);

            ApplyCommandDelay(payload8);
            // Reply layout mirrors the request: [reportId] op addr_hi addr_lo data0 ...
            return reply[4..9];
        }

        // :290-303
        public static int CommandDelay(byte[] payload8)
        {
            int delay = (payload8[0], payload8[1]) switch
            {
                (0xA6, 0x07) => 105,   // power on
                (0xA6, 0x05) => 15,    // video off/on
                (0xA6, 0x03) => 15,
                (0xA6, 0x01) => 15,    // input info
                (0xA6, 0x02) => 25,    // output info
                (0xB5, 0x00) when payload8[2] == 0x32 => 108,
                _ => 3,
            };
            return delay;
        }

        private void ApplyCommandDelay(byte[] payload8) => Delays.Add(CommandDelay(payload8));

        // :306-310
        public bool IsPanelConnected()
        {
            try { return SendReport(PanelStatusReport)[0] == 1; }
            catch { return false; }
        }

        // :316-320
        public void PollPanelStatus()
        {
            try { SendReport(PanelStatusReport); }
            catch { }
        }

        // :326-344
        public void SetMode(int width, int height, byte vic)
        {
            byte wHi = (byte)(width >> 8), wLo = (byte)(width & 0xFF);
            byte hHi = (byte)(height >> 8), hLo = (byte)(height & 0xFF);

            SendReport(PanelStatusReport);
            SendReport([0xB5, 0xF4, 0x39, 0, 0, 0, 0, 0]);
            SendReport([0xA6, 0x03, 0x03, 0, 0, 0, 0, 0]);
            SendReport([0xB5, 0xC5, 0x58, 0, 0, 0, 0, 0]);
            SendReport([0xA6, 0x01, wHi, wLo, hHi, hLo, 0x11, 0x00]); // in: RGB888
            SendReport([0xB5, 0xC5, 0x56, 0, 0, 0, 0, 0]);
            SendReport([0xA6, 0x02, vic, 0x00, wHi, wLo, hHi, hLo]);  // out: VIC
            SendReport([0xB5, 0xC5, 0x57, 0, 0, 0, 0, 0]);
            SendReport([0xA6, 0x04, 0x01, 0, 0, 0, 0, 0]);            // accept frames
            Delays.Add(FirstFrameDelayMs);

            _videoEnabled = false;
        }

        // :350-359
        public void EnableVideo()
        {
            if (_videoEnabled) return;
            _videoEnabled = true;

            foreach (var report in EnableVideoReports)
                SendReport(report);
        }

        // :361-365
        public void PowerOff()
        {
            try { SendReport([0xA6, 0x07, 0, 0, 0, 0, 0, 0]); }
            catch { }
        }

        // :371-422
        public void SendFrame(byte[] bgrPixels, int width, int height)
        {
            int payloadLen = width * height * 3;
            if (bgrPixels.Length < payloadLen)
                throw new ArgumentException($"Pixel buffer too small: {bgrPixels.Length} < {payloadLen}");

            int total = 8 + payloadLen + 8;
            var buffer = new byte[total]; // 1.4.x rented it from ArrayPool<byte>.Shared: the same bytes up to total
            {
                // Header: FF 00, x/16, y BE16, width/16 (rounded down, per OEM capture), height | 0x8000.
                buffer[0] = 0xFF;
                buffer[1] = 0x00;
                buffer[2] = 0x00;                       // x/16
                buffer[3] = 0x00;                       // y hi
                buffer[4] = 0x00;                       // y lo
                buffer[5] = (byte)(width / 16);
                int flaggedHeight = height | 0x8000;
                buffer[6] = (byte)(flaggedHeight >> 8);
                buffer[7] = (byte)(flaggedHeight & 0xFF);

                Buffer.BlockCopy(bgrPixels, 0, buffer, 8, payloadLen);

                // Trailer: FF C0 00 00 00 00 00 00
                int t = 8 + payloadLen;
                buffer[t] = 0xFF;
                buffer[t + 1] = 0xC0;
                for (int i = 2; i < 8; i++) buffer[t + i] = 0;

                // Write in 64 KB chunks, matching the OEM app's transfer pattern.
                int offset = 0;
                while (offset < total)
                {
                    int len = Math.Min(ChunkSize, total - offset);
                    Chunks.Add(buffer[offset..(offset + len)]);
                    if (!Writer(Chunks.Count - 1))
                    {
                        // The device has consumed part of this frame; abort the pipe so the
                        // next frame starts at a header instead of mid-payload.
                        Resets++;
                        throw new Exception($"USB bulk write failed at offset {offset}/{total}");
                    }
                    offset += len;
                }
            }
        }

        public static int WriteTimeout => WriteTimeoutMs;
    }
}
