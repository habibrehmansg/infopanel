using HidSharp;
using Serilog;
using System;
using System.Linq;

namespace InfoPanel.ThermalrightPanel
{
    /// <summary>
    /// HID-based panel communication for Thermalright Trofeo Vision panels.
    /// Protocol reverse-engineered from Thermal Engine (github.com/nathanielhernandez/Thermal-Engine).
    /// Uses DA DB DC DD magic, 512-byte packets with 0x00 report ID prefix.
    /// </summary>
    public class HidPanelDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<HidPanelDevice>();

        private static readonly byte[] MAGIC_BYTES = { 0xDA, 0xDB, 0xDC, 0xDD };
        private const int PACKET_SIZE = 512;
        private const int HEADER_JPEG_OFFSET = 20; // JPEG data starts at byte 20 in header packet

        private HidStream? _stream;
        private bool _disposed;

        public bool IsOpen => _stream != null;

        /// <summary>
        /// Opens a Trofeo Vision HID device by VID/PID.
        /// </summary>
        public static HidPanelDevice? Open(int vendorId, int productId)
        {
            Logger.Information("HidPanelDevice: Scanning for HID device VID={Vid:X4} PID={Pid:X4}", vendorId, productId);

            var deviceList = DeviceList.Local;
            var hidDevices = deviceList.GetHidDevices(vendorId, productId).ToList();

            if (hidDevices.Count == 0)
            {
                Logger.Warning("HidPanelDevice: No HID device found for VID={Vid:X4} PID={Pid:X4}", vendorId, productId);
                return null;
            }

            Logger.Information("HidPanelDevice: Found {Count} HID device(s), trying first", hidDevices.Count);

            foreach (var device in hidDevices)
            {
                try
                {
                    Logger.Debug("HidPanelDevice: Trying device: {Path}, MaxOutputReportLength={MaxOut}",
                        device.DevicePath, device.GetMaxOutputReportLength());

                    var stream = device.Open();
                    stream.WriteTimeout = 5000;
                    stream.ReadTimeout = 5000;

                    Logger.Information("HidPanelDevice: Opened device at {Path}", device.DevicePath);

                    return new HidPanelDevice { _stream = stream };
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "HidPanelDevice: Failed to open device at {Path}", device.DevicePath);
                }
            }

            Logger.Error("HidPanelDevice: Could not open any HID device");
            return null;
        }

        /// <summary>
        /// Sends the init command (DA DB DC DD magic, 0x01 at offset 12).
        /// </summary>
        public bool SendInit()
        {
            if (_stream == null) return false;

            try
            {
                var packet = new byte[PACKET_SIZE];
                // Bytes 0-3: Magic
                Array.Copy(MAGIC_BYTES, 0, packet, 0, 4);
                // Byte 4: 0x00 (already zero)
                // Byte 12: Init flag = 0x01
                packet[12] = 0x01;

                WritePacket(packet);
                Logger.Information("HidPanelDevice: Init command sent");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "HidPanelDevice: Failed to send init");
                return false;
            }
        }

        /// <summary>
        /// Reads the init response from the device.
        /// Returns the raw response bytes, or null on failure.
        /// </summary>
        public byte[]? ReadInitResponse()
        {
            if (_stream == null) return null;

            try
            {
                var buffer = new byte[PACKET_SIZE + 1]; // +1 for report ID
                int bytesRead = _stream.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    // Skip report ID byte (first byte)
                    var response = new byte[bytesRead - 1];
                    Array.Copy(buffer, 1, response, 0, response.Length);
                    Logger.Information("HidPanelDevice: Init response ({Bytes} bytes): {Hex}",
                        response.Length, BitConverter.ToString(response, 0, Math.Min(response.Length, 36)).Replace("-", " "));
                    return response;
                }
            }
            catch (TimeoutException)
            {
                Logger.Debug("HidPanelDevice: No init response (timeout)");
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "HidPanelDevice: Error reading init response");
            }

            return null;
        }

        /// <summary>
        /// Sends a JPEG frame using the Trofeo HID protocol.
        /// Header packet (512 bytes): magic[0:3], cmd=0x02[4], resolution[8:11], type=0x02[12], jpeg_size[16:19], jpeg_data[20:511]
        /// Remaining JPEG data sent in 512-byte chunks.
        /// </summary>
        public bool SendJpegFrame(byte[] jpegData, int width, int height)
        {
            if (_stream == null) return false;

            try
            {
                // Build header packet (512 bytes)
                var header = new byte[PACKET_SIZE];

                // Bytes 0-3: Magic DA DB DC DD
                Array.Copy(MAGIC_BYTES, 0, header, 0, 4);

                // Byte 4: Frame command = 0x02
                header[4] = 0x02;

                // Bytes 8-9: Width (uint16 LE)
                BitConverter.GetBytes((ushort)width).CopyTo(header, 8);

                // Bytes 10-11: Height (uint16 LE)
                BitConverter.GetBytes((ushort)height).CopyTo(header, 10);

                // Byte 12: Frame type indicator = 0x02
                header[12] = 0x02;

                // Bytes 16-19: JPEG data size (uint32 LE)
                BitConverter.GetBytes(jpegData.Length).CopyTo(header, 16);

                // Bytes 20-511: First portion of JPEG data
                int firstChunkSize = Math.Min(jpegData.Length, PACKET_SIZE - HEADER_JPEG_OFFSET);
                Array.Copy(jpegData, 0, header, HEADER_JPEG_OFFSET, firstChunkSize);

                WritePacket(header);

                // Send remaining JPEG data in 512-byte chunks
                int offset = firstChunkSize;
                while (offset < jpegData.Length)
                {
                    var chunk = new byte[PACKET_SIZE];
                    int chunkSize = Math.Min(jpegData.Length - offset, PACKET_SIZE);
                    Array.Copy(jpegData, offset, chunk, 0, chunkSize);
                    // Remaining bytes are already zero-padded

                    WritePacket(chunk);
                    offset += chunkSize;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "HidPanelDevice: Failed to send JPEG frame ({Size} bytes)", jpegData.Length);
                throw;
            }
        }

        /// <summary>
        /// Writes a 512-byte packet to the HID device, prepending the 0x00 report ID.
        /// </summary>
        private void WritePacket(byte[] data)
        {
            if (_stream == null) throw new InvalidOperationException("Device not open");

            // HID write requires report ID prefix (0x00)
            var report = new byte[data.Length + 1];
            report[0] = 0x00; // Report ID
            Array.Copy(data, 0, report, 1, data.Length);

            _stream.Write(report, 0, report.Length);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
