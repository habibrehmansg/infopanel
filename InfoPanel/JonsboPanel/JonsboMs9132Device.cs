using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace InfoPanel.JonsboPanel
{
    /// <summary>
    /// MacroSilicon MS9132 USB display transport, used by the Jonsbo DS339 (376x960).
    /// Protocol from the ms912x/ms9132 Linux drivers plus a USB capture of the OEM
    /// JONSBO-AIO app driving a real DS339 (see JONSBO-AIO PROTOCOL.md):
    ///
    ///   * Control plane rides the HID interface (MI_00) as 8-byte feature reports:
    ///       A6 [op] [6 bytes]  writes, B5 [addr_be16] reads, B6 [addr_be16] [value] writes,
    ///       C5/C6 xdata address/value pairs, F5 flash reads. Every report is a SET_REPORT
    ///       immediately followed by a GET_REPORT, even when the reply is discarded.
    ///   * Frames go to bulk OUT EP 0x04 on the vendor interface (MI_03, "msusb video",
    ///     needs the OEM's libusb driver or WinUSB):
    ///       8-byte header: FF 00 | x/16 (u8) | y (be16) | width/16 (u8) | height|0x8000 (be16)
    ///       + width*height*3 bytes BGR888 + 8-byte trailer FF C0 00 00 00 00 00 00,
    ///     written in 64 KB chunks. Width byte rounds down (376/16 -> 23), as captured;
    ///     the payload still carries all 376 columns (verified: 17 URBs, 1,082,896 B/frame).
    ///
    /// The startup sequence below is the OEM capture replayed verbatim, in three stages:
    /// <see cref="Open"/> runs the power-up + register + EDID block, <see cref="SetMode"/>
    /// programs the timing and ends at A6 04 01, and <see cref="EnableVideo"/> turns the
    /// output on. The OEM only sends A6 05 01 *after* the first frames are on the wire;
    /// enabling video before the framebuffer holds data leaves the scaler refusing to
    /// drain EP 0x04, which surfaces as "USB bulk write failed: IoTimedOut (0/65536)"
    /// (habibrehmansg/infopanel#166).
    /// </summary>
    public sealed class JonsboMs9132Device : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<JonsboMs9132Device>();

        public const int VENDOR_ID = 0x345F;
        public const int PRODUCT_ID = 0x9132;

        private const int ChunkSize = 65536;
        private const int WriteTimeoutMs = 5000;

        /// <summary>Gap the OEM app leaves between A6 04 01 and the first frame.</summary>
        private const int FirstFrameDelayMs = 400;

        /// <summary>Power-up, register setup and EDID read block (OEM steps 1-56).</summary>
        private static readonly byte[][] StartupReports =
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

        /// <summary>Output enable, sent once frames are flowing (OEM steps 67-70).</summary>
        private static readonly byte[][] EnableVideoReports =
        [
            [0xB5, 0xF0, 0x05, 0, 0, 0, 0, 0],
            [0xB6, 0xF0, 0x05, 0x30, 0, 0, 0, 0],   // display control, output bit set
            [0xA6, 0x05, 0x01, 0, 0, 0, 0, 0],      // video on
            [0xB5, 0xC5, 0x55, 0, 0, 0, 0, 0],
        ];

        private static readonly byte[] PanelStatusReport = [0xB5, 0x00, 0x32, 0, 0, 0, 0, 0];

        private HidStream? _hidStream;
        private UsbDevice? _usbDevice;
        private UsbEndpointWriter? _writer;
        private bool _videoEnabled;
        private bool _disposed;

        public string SerialNumber { get; private set; } = string.Empty;

        /// <summary>
        /// Powers the panel down on dispose. Cleared while retrying after a transport
        /// error so the panel is not visibly power-cycled on every attempt.
        /// </summary>
        public bool PowerOffOnDispose { get; set; } = true;

        public static JonsboMs9132Device? Open()
        {
            var dev = new JonsboMs9132Device();
            try
            {
                // Control plane: the HID interface, via the inbox HID driver. Some systems
                // expose more than one collection for this device, so try each until one
                // accepts the startup block instead of trusting enumeration order.
                var hidDevices = DeviceList.Local.GetHidDevices(VENDOR_ID, PRODUCT_ID).ToList();
                if (hidDevices.Count == 0)
                {
                    Logger.Warning("JonsboMs9132: HID interface not found (VID={Vid:X4} PID={Pid:X4})", VENDOR_ID, PRODUCT_ID);
                    dev.Dispose();
                    return null;
                }

                foreach (var hidDevice in hidDevices)
                {
                    if (!hidDevice.TryOpen(out var hidStream))
                    {
                        Logger.Debug("JonsboMs9132: Cannot open HID interface {Path}", hidDevice.DevicePath);
                        continue;
                    }

                    try
                    {
                        hidStream.ReadTimeout = 1000;
                        hidStream.WriteTimeout = 1000;
                        dev._hidStream = hidStream;
                        dev.RunStartupBlock();
                        try { dev.SerialNumber = hidDevice.GetSerialNumber(); } catch { }
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(ex, "JonsboMs9132: Startup block rejected by {Path}", hidDevice.DevicePath);
                        dev._hidStream = null;
                        hidStream.Dispose();
                    }
                }

                if (dev._hidStream == null)
                {
                    Logger.Warning("JonsboMs9132: No HID interface accepted the startup sequence");
                    dev.Dispose();
                    return null;
                }

                // Frame pipe: the vendor "msusb video" interface via libusb/WinUSB.
                var finder = new UsbDeviceFinder(VENDOR_ID, PRODUCT_ID);
                dev._usbDevice = UsbDevice.OpenUsbDevice(finder);
                if (dev._usbDevice == null)
                {
                    Logger.Warning("JonsboMs9132: Video interface not found. Is the Jonsbo MSUSBDisplay driver installed on interface 3?");
                    dev.Dispose();
                    return null;
                }

                if (dev._usbDevice is IUsbDevice wholeDevice)
                {
                    wholeDevice.SetConfiguration(1);
                    wholeDevice.ClaimInterface(3);
                }

                dev._writer = dev._usbDevice.OpenEndpointWriter(WriteEndpointID.Ep04);

                Logger.Information("JonsboMs9132: Opened (serial {Serial})", dev.SerialNumber);
                return dev;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "JonsboMs9132: Open failed");
                dev.Dispose();
                return null;
            }
        }

        private void RunStartupBlock()
        {
            foreach (var report in StartupReports)
                SendReport(report);
        }

        /// <summary>
        /// Sends one 8-byte feature report and reads the reply back, which is what the OEM
        /// app does for every command. Returns the 5 data bytes that follow the opcode echo.
        /// </summary>
        private byte[] SendReport(byte[] payload8)
        {
            if (_hidStream == null) throw new InvalidOperationException("HID not open");

            // Feature report: [reportId=0] + 8 payload bytes.
            var buffer = new byte[9];
            Array.Copy(payload8, 0, buffer, 1, Math.Min(payload8.Length, 8));
            _hidStream.SetFeature(buffer);

            var reply = new byte[9];
            _hidStream.GetFeature(reply);

            ApplyCommandDelay(payload8);
            // Reply layout mirrors the request: [reportId] op addr_hi addr_lo data0 ...
            return reply[4..9];
        }

        /// <summary>Inter-command pacing taken from the OEM capture.</summary>
        private static void ApplyCommandDelay(byte[] payload8)
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
            Thread.Sleep(delay);
        }

        /// <summary>Panel connection status (register 0x32): 1 = connected.</summary>
        public bool IsPanelConnected()
        {
            try { return SendReport(PanelStatusReport)[0] == 1; }
            catch { return false; }
        }

        /// <summary>
        /// Status poll the OEM app issues once per frame while streaming. Failures are
        /// swallowed: the frame pipe is what matters.
        /// </summary>
        public void PollPanelStatus()
        {
            try { SendReport(PanelStatusReport); }
            catch (Exception ex) { Logger.Debug(ex, "JonsboMs9132: Status poll failed"); }
        }

        /// <summary>
        /// Applies the captured OEM mode-set sequence for the given native resolution and
        /// VIC. Video output stays off; call <see cref="EnableVideo"/> once frames are flowing.
        /// </summary>
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
            Thread.Sleep(FirstFrameDelayMs);

            _videoEnabled = false;
            Logger.Information("JonsboMs9132: Mode set {Width}x{Height} VIC {Vic}", width, height, vic);
        }

        /// <summary>
        /// Turns the panel output on. Must run after the first frames have been written,
        /// matching the OEM app; doing it earlier stalls the bulk pipe on some units.
        /// </summary>
        public void EnableVideo()
        {
            if (_videoEnabled) return;
            _videoEnabled = true;

            foreach (var report in EnableVideoReports)
                SendReport(report);

            Logger.Information("JonsboMs9132: Video output enabled");
        }

        public void PowerOff()
        {
            try { SendReport([0xA6, 0x07, 0, 0, 0, 0, 0, 0]); }
            catch (Exception ex) { Logger.Debug(ex, "JonsboMs9132: PowerOff failed"); }
        }

        /// <summary>
        /// Sends one full frame. <paramref name="bgrPixels"/> must be width*height*3 bytes,
        /// B,G,R order, top-down rows at the panel's native (portrait) resolution.
        /// </summary>
        public void SendFrame(byte[] bgrPixels, int width, int height)
        {
            if (_writer == null) throw new InvalidOperationException("USB not open");

            int payloadLen = width * height * 3;
            if (bgrPixels.Length < payloadLen)
                throw new ArgumentException($"Pixel buffer too small: {bgrPixels.Length} < {payloadLen}");

            int total = 8 + payloadLen + 8;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
            try
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
                    var ec = _writer.Write(buffer, offset, len, WriteTimeoutMs, out int written);
                    if (ec != ErrorCode.None || written != len)
                    {
                        // The device has consumed part of this frame; abort the pipe so the
                        // next frame starts at a header instead of mid-payload.
                        try { _writer.Reset(); } catch { }
                        throw new Exception($"USB bulk write failed: {ec} ({written}/{len}) at offset {offset}/{total}");
                    }
                    offset += len;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (PowerOffOnDispose) PowerOff();
            try { _hidStream?.Dispose(); } catch { }
            _hidStream = null;
            try
            {
                if (_usbDevice != null)
                {
                    if (_usbDevice.IsOpen)
                    {
                        if (_usbDevice is IUsbDevice wholeDevice)
                        {
                            try { wholeDevice.ReleaseInterface(3); } catch { }
                        }
                        _usbDevice.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "JonsboMs9132: Dispose error");
            }
            _usbDevice = null;
            _writer = null;
        }
    }
}
