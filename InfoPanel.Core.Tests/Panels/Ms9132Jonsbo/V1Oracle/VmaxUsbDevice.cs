// V1 oracle, test-only: the protocol code of InfoPanel 1.4.x c71f0f00, InfoPanel/VmaxPanel/VmaxUsbDevice.cs, copied
// verbatim from the lines cited above each part, but for these changes: log lines are left out; the HID stream's
// SetFeature is a capture into Reports and its GetFeature fills the buffer from GetFeature (the test's device); a
// Thread.Sleep is a capture into Delays (timing, not bytes); the bulk writer is a capture into Chunks, each answered by
// Writer; the clock of the status poll is Now (1.4.x read DateTime.UtcNow); FindWriteEndpoint reads a list of
// interfaces' endpoint addresses (1.4.x read LibUsbDotNet's Configs). Open, Dispose and the HID and libusb lookups are
// device code and left out; InitializeStreamingMode is its one collection's part (the loop over collections is the
// driver's). No device code, nothing from Windows.
namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle
{
    public sealed class V1VmaxUsbDevice
    {
        // :15-86
        public static readonly byte[] FramePrefix = [0xFF, 0x00, 0x00, 0x00, 0x00, 0x14, 0x03, 0xC0];
        public static readonly byte[] FrameSuffix = [0xFF, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        public static readonly byte[] StreamingStatusFeatureReport = [0xB5, 0x00, 0x32, 0x00, 0x00, 0x00, 0x00, 0x00];
        public static readonly byte[] StreamingPulseFeatureReport = [0xB5, 0xC5, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00];
        public static readonly byte[] DisplayEnableFeatureReport = [0xA6, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];
        public static readonly byte[][] StreamingStartupFeatureReports =
        [
            [0xB5, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0x00, 0x31, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0x00, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF2, 0x42, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF4, 0x39, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xF5, 0x00, 0x1F, 0xE0, 0x00, 0x00, 0x00, 0x00],
            [0xF5, 0x00, 0x1F, 0xE8, 0x00, 0x00, 0x00, 0x00],
            [0xF5, 0x00, 0x1F, 0xF0, 0x00, 0x00, 0x00, 0x00],
            [0xF5, 0x00, 0x1F, 0xF8, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x07, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC4, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC5, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB6, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC5, 0xB0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC6, 0xB0, 0xDF, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC5, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC6, 0xA0, 0xE4, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC5, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC6, 0xA0, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF0, 0x16, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB6, 0xF0, 0x16, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0x00, 0x32, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x38, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x3C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x48, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x4C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x58, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x5C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x68, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x6C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x70, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x74, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x78, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x7C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF4, 0x39, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x03, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC5, 0x58, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x01, 0x01, 0x40, 0x03, 0xC0, 0x11, 0x00],
            [0xB5, 0xC5, 0x56, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x02, 0xB2, 0x00, 0x01, 0x40, 0x03, 0xC0],
            [0xB5, 0xC5, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00],
        ];

        // :88-91
        public const int InterfaceNumber = 3;
        public const int UsbWriteTimeoutMs = 1000;
        public const int UsbWriteChunkSize = 65536;
        public const int StreamingInitDelayMs = 25;

        // :97-101
        private DateTime _lastStreamingStatusPolled = DateTime.MinValue;
        private byte[]? _frameBuffer;
        private bool _hasSentFrame;
        private bool? _currentScreenSwitch;

        /// <summary>Every feature report set, in order, as the 9 bytes SetFeature was given.</summary>
        public readonly List<byte[]> Reports = new();

        /// <summary>The test's device: fills GetFeature's buffer for the report last set.</summary>
        public Action<byte[], byte[]> GetFeature = (_, _) => { };

        /// <summary>Every Thread.Sleep, in milliseconds, in order.</summary>
        public readonly List<int> Delays = new();

        /// <summary>Every bulk write's bytes, in order.</summary>
        public readonly List<byte[]> Chunks = new();

        public Func<int, bool> Writer = _ => true;

        /// <summary>The status poll's clock.</summary>
        public Func<DateTime> Now = () => DateTime.UtcNow;

        // :164-192, over (interface, endpoint addresses) in descriptor order
        public static byte FindWriteEndpoint(IEnumerable<(int Interface, byte[] Endpoints)> interfaces)
        {
            byte endpoint = 0x04;

            foreach (var iface in interfaces)
            {
                foreach (var ep in iface.Endpoints)
                {
                    var endpointId = ep;
                    if ((endpointId & 0x80) != 0)
                        continue;

                    if (endpointId == 0x04)
                        return endpointId;

                    endpoint = endpointId;
                }
            }

            return endpoint;
        }

        // :194-256, one collection's part (the try block): the reports sent and their delays
        public void InitializeStreamingMode()
        {
            byte[] response = [];
            foreach (var featureReport in StreamingStartupFeatureReports)
            {
                response = SendFeatureReport(featureReport);
                ApplyStartupDelay(featureReport);
            }

            for (var i = 0; i < 42; i++)
            {
                response = SendFeatureReport(StreamingPulseFeatureReport);
                Delays.Add(2);
            }

            response = SendFeatureReport(DisplayEnableFeatureReport);
            Delays.Add(StreamingInitDelayMs);
            response = SendFeatureReport(StreamingStatusFeatureReport);

            Delays.Add(StreamingInitDelayMs);
        }

        // :258-270
        public byte[] SendFeatureReport(byte[] payload)
        {
            var report = new byte[payload.Length + 1];
            report[0] = 0x00;
            Buffer.BlockCopy(payload, 0, report, 1, payload.Length);

            Reports.Add(report);

            var response = new byte[report.Length];
            response[0] = 0x00;
            GetFeature(report, response);
            return response;
        }

        // :272-293
        public static int StartupDelay(byte[] featureReport)
        {
            if (IsFeatureReport(featureReport, 0xA6, 0x07, 0x01))
            {
                return 105;
            }

            if (IsFeatureReport(featureReport, 0xA6, 0x05, 0x00))
            {
                return 15;
            }

            if (IsFeatureReport(featureReport, 0xB5, 0x00, 0x32))
            {
                return 108;
            }

            return 1;
        }

        private void ApplyStartupDelay(byte[] featureReport) => Delays.Add(StartupDelay(featureReport));

        // :295-301
        private static bool IsFeatureReport(byte[] featureReport, byte first, byte second, byte third)
        {
            return featureReport.Length >= 3
                && featureReport[0] == first
                && featureReport[1] == second
                && featureReport[2] == third;
        }

        // :308-339
        public void SendRgb888Frame(byte[] rgbData, int rgbDataLength)
        {
            if (rgbDataLength < 0 || rgbDataLength > rgbData.Length)
                throw new ArgumentOutOfRangeException(nameof(rgbDataLength));

            var frameLength = FramePrefix.Length + rgbDataLength + FrameSuffix.Length;
            if (_frameBuffer == null || _frameBuffer.Length != frameLength)
            {
                _frameBuffer = new byte[frameLength];
                Buffer.BlockCopy(FramePrefix, 0, _frameBuffer, 0, FramePrefix.Length);
                Buffer.BlockCopy(FrameSuffix, 0, _frameBuffer, FramePrefix.Length + rgbDataLength, FrameSuffix.Length);
            }

            Buffer.BlockCopy(rgbData, 0, _frameBuffer, FramePrefix.Length, rgbDataLength);

            var offset = 0;
            while (offset < _frameBuffer.Length)
            {
                var writeSize = Math.Min(UsbWriteChunkSize, _frameBuffer.Length - offset);
                Chunks.Add(_frameBuffer[offset..(offset + writeSize)]);
                if (!Writer(Chunks.Count - 1))
                {
                    throw new InvalidOperationException($"VMAX USB write failed at offset {offset}/{_frameBuffer.Length}.");
                }

                offset += writeSize;
            }

            _hasSentFrame = true;
            PollStreamingStatusIfDue();
        }

        // :341-365
        public void SetScreenSwitch(bool enabled)
        {
            if (!_hasSentFrame || _currentScreenSwitch == enabled)
                return;

            try
            {
                byte[] response = [];
                foreach (var featureReport in GetScreenSwitchFeatureReports(enabled))
                {
                    response = SendFeatureReport(featureReport);
                    ApplyStartupDelay(featureReport);
                }

                _currentScreenSwitch = enabled;
            }
            catch
            {
            }
        }

        // :367-373
        public static byte[][] GetScreenSwitchFeatureReports(bool enabled) =>
        [
            [0xB5, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB6, 0xF0, 0x05, enabled ? (byte)0x10 : (byte)0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x05, enabled ? (byte)0x01 : (byte)0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC5, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00],
        ];

        // :375-395
        private void PollStreamingStatusIfDue()
        {
            var now = Now();
            if ((now - _lastStreamingStatusPolled).TotalMilliseconds < 500)
                return;

            _lastStreamingStatusPolled = now;

            try
            {
                var response = SendFeatureReport(StreamingStatusFeatureReport);
            }
            catch
            {
            }
        }
    }
}
