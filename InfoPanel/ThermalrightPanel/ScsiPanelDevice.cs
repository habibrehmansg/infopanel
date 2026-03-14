using Microsoft.Win32.SafeHandles;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace InfoPanel.ThermalrightPanel
{
    /// <summary>
    /// SCSI pass-through wrapper for Thermalright LCD panels that present as USB Mass Storage devices.
    /// Uses IOCTL_SCSI_PASS_THROUGH_DIRECT to send F5-prefixed CDB commands.
    /// Protocol reference: Lexonight1/thermalright-trcc-linux USBLCD_PROTOCOL.md
    /// </summary>
    public sealed class ScsiPanelDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ScsiPanelDevice));

        // SCSI protocol constants
        private const int POLL_BUFFER_SIZE = 0xE100;   // 57,600 bytes — poll/init buffer size
        private const int FRAME_CHUNK_SIZE = 0x10000;  // 65,536 bytes — 64KB frame chunks
        private const byte SCSI_PROTOCOL_MARKER = 0xF5;
        private const byte SCSI_IOCTL_DATA_IN = 1;
        private const byte SCSI_IOCTL_DATA_OUT = 0;

        // Win32 constants
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x4D014;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

        // Storage property query constants
        private const int StorageDeviceProperty = 0;
        private const int PropertyStandardQuery = 0;

        #region P/Invoke

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        #endregion

        #region Native Structures

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public int PropertyId;
            public int QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            public byte RemovableMedia;
            public byte CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public byte BusType;
            public uint RawPropertiesLength;
            // Followed by variable-length raw data
        }

        // SCSI_PASS_THROUGH_DIRECT structure layout on x64 (written directly to unmanaged memory):
        //   Offset  Size  Field
        //   0       2     Length (= 56)
        //   2       1     ScsiStatus
        //   6       1     CdbLength
        //   7       1     SenseInfoLength
        //   8       1     DataIn
        //   12      4     DataTransferLength
        //   16      4     TimeOutValue
        //   24      8     DataBuffer (pointer)
        //   32      4     SenseInfoOffset (= 56)
        //   36      16    Cdb
        //   56      32    SenseBuf (appended)
        //   Total: 88 bytes

        #endregion

        private SafeFileHandle _handle;
        private readonly string _devicePath;

        private ScsiPanelDevice(SafeFileHandle handle, string devicePath)
        {
            _handle = handle;
            _devicePath = devicePath;
        }

        /// <summary>
        /// Information about a discovered SCSI LCD panel device.
        /// </summary>
        public class ScsiDeviceInfo
        {
            public string DevicePath { get; set; } = string.Empty;
            public string VendorId { get; init; } = string.Empty;
            public string ProductId { get; init; } = string.Empty;
        }

        /// <summary>
        /// Enumerates PhysicalDrive0-15 and returns any that have "USBLCD" as the SCSI vendor string.
        /// </summary>
        public static List<ScsiDeviceInfo> FindDevices()
        {
            var devices = new List<ScsiDeviceInfo>();

            for (int i = 0; i < 16; i++)
            {
                var path = $"\\\\.\\PhysicalDrive{i}";
                try
                {
                    using var handle = CreateFile(path,
                        GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (handle.IsInvalid)
                        continue;

                    var info = QueryStorageDeviceDescriptor(handle);
                    if (info == null)
                        continue;

                    if (info.VendorId.Contains("USBLCD", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Information("ScsiPanelDevice: Found USBLCD device at {Path} (Vendor={Vendor}, Product={Product})",
                            path, info.VendorId, info.ProductId);
                        info.DevicePath = path;
                        devices.Add(info);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("ScsiPanelDevice: Error probing {Path}: {Error}", path, ex.Message);
                }
            }

            return devices;
        }

        /// <summary>
        /// Queries the storage device descriptor for a given device handle to get vendor/product strings.
        /// </summary>
        private static ScsiDeviceInfo? QueryStorageDeviceDescriptor(SafeFileHandle handle)
        {
            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageDeviceProperty,
                QueryType = PropertyStandardQuery
            };

            int querySize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
            int outputSize = 1024; // Large enough for descriptor + strings
            var queryPtr = Marshal.AllocHGlobal(querySize);
            var outputPtr = Marshal.AllocHGlobal(outputSize);

            try
            {
                Marshal.StructureToPtr(query, queryPtr, false);

                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    queryPtr, (uint)querySize,
                    outputPtr, (uint)outputSize,
                    out _, IntPtr.Zero))
                {
                    return null;
                }

                var descriptor = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(outputPtr);

                string vendorId = descriptor.VendorIdOffset > 0
                    ? Marshal.PtrToStringAnsi(outputPtr + (int)descriptor.VendorIdOffset)?.Trim() ?? ""
                    : "";

                string productId = descriptor.ProductIdOffset > 0
                    ? Marshal.PtrToStringAnsi(outputPtr + (int)descriptor.ProductIdOffset)?.Trim() ?? ""
                    : "";

                return new ScsiDeviceInfo { VendorId = vendorId, ProductId = productId };
            }
            finally
            {
                Marshal.FreeHGlobal(queryPtr);
                Marshal.FreeHGlobal(outputPtr);
            }
        }

        /// <summary>
        /// Opens a SCSI panel device at the given path (e.g., \\.\PhysicalDrive1).
        /// </summary>
        public static ScsiPanelDevice? Open(string devicePath)
        {
            var handle = CreateFile(devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Warning("ScsiPanelDevice: Failed to open {Path}, Win32 error {Error}", devicePath, error);
                return null;
            }

            Logger.Information("ScsiPanelDevice: Opened {Path}", devicePath);
            return new ScsiPanelDevice(handle, devicePath);
        }

        /// <summary>
        /// Sends a SCSI TEST UNIT READY command (CDB opcode 0x00, 6 bytes, no data).
        /// Returns true if the device responds, false on timeout/error.
        /// Used as a diagnostic to verify the SCSI pass-through path works at all.
        /// </summary>
        public bool TestUnitReady()
        {
            var cdb = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            var noData = Array.Empty<byte>();
            return SendScsiCommand(cdb, noData, SCSI_IOCTL_DATA_IN);
        }

        /// <summary>
        /// Polls the device by sending CDB F5 00 00 00, reading 0xE100 bytes.
        /// Returns the poll response or null on failure.
        /// </summary>
        public byte[]? Poll()
        {
            var cdb = new byte[] { SCSI_PROTOCOL_MARKER, 0x00, 0x00, 0x00, 0x00, 0x00 };

            var response = new byte[POLL_BUFFER_SIZE];
            if (SendScsiCommand(cdb, response, SCSI_IOCTL_DATA_IN))
                return response;

            return null;
        }

        /// <summary>
        /// Checks if poll response indicates device is still booting (bytes 4-7 = 0xA1A2A3A4).
        /// </summary>
        public static bool IsDeviceBooting(byte[] pollResponse)
        {
            return pollResponse.Length >= 8
                && pollResponse[4] == 0xA1
                && pollResponse[5] == 0xA2
                && pollResponse[6] == 0xA3
                && pollResponse[7] == 0xA4;
        }

        /// <summary>
        /// Initializes the display controller by sending CDB F5 01 00 00 with 0xE100 zero bytes.
        /// </summary>
        public bool Init()
        {
            var cdb = new byte[] { SCSI_PROTOCOL_MARKER, 0x01, 0x00, 0x00, 0x00, 0x00 };

            var data = new byte[POLL_BUFFER_SIZE]; // 0xE100 zero bytes
            return SendScsiCommand(cdb, data, SCSI_IOCTL_DATA_OUT);
        }

        /// <summary>
        /// Sends a complete RGB565 frame by splitting it into 64KB chunks.
        /// CDB: F5 01 01 [chunk_index] for each chunk.
        /// </summary>
        public bool SendFrame(byte[] rgb565Data)
        {
            int offset = 0;
            int chunkIndex = 0;

            while (offset < rgb565Data.Length)
            {
                int remaining = rgb565Data.Length - offset;
                int chunkSize = Math.Min(FRAME_CHUNK_SIZE, remaining);

                var cdb = new byte[] { SCSI_PROTOCOL_MARKER, 0x01, 0x01, (byte)chunkIndex, 0x00, 0x00 };

                var chunk = new byte[chunkSize];
                Array.Copy(rgb565Data, offset, chunk, 0, chunkSize);

                if (!SendScsiCommand(cdb, chunk, SCSI_IOCTL_DATA_OUT))
                {
                    Logger.Warning("ScsiPanelDevice: Failed to send frame chunk {Index} ({Size} bytes)",
                        chunkIndex, chunkSize);
                    return false;
                }

                offset += chunkSize;
                chunkIndex++;
            }

            return true;
        }

        /// <summary>
        /// Sends a SCSI CDB command with data transfer via IOCTL_SCSI_PASS_THROUGH_DIRECT.
        /// Writes the SCSI_PASS_THROUGH_DIRECT structure directly to unmanaged memory
        /// to avoid potential issues with Marshal.StructureToPtr and unsafe fixed buffers.
        /// </summary>
        private bool SendScsiCommand(byte[] cdb, byte[] data, byte direction)
        {
            // Pin the data buffer so GC doesn't move it during the ioctl
            var dataHandle = data.Length > 0 ? GCHandle.Alloc(data, GCHandleType.Pinned) : default;
            try
            {
                // SCSI_PASS_THROUGH_DIRECT layout on x64:
                //   0: USHORT Length          (= 56, size of SCSI_PASS_THROUGH_DIRECT)
                //   2: UCHAR  ScsiStatus
                //   3: UCHAR  PathId
                //   4: UCHAR  TargetId
                //   5: UCHAR  Lun
                //   6: UCHAR  CdbLength
                //   7: UCHAR  SenseInfoLength
                //   8: UCHAR  DataIn
                //  12: ULONG  DataTransferLength
                //  16: ULONG  TimeOutValue
                //  24: PVOID  DataBuffer       (8 bytes on x64)
                //  32: ULONG  SenseInfoOffset
                //  36: UCHAR  Cdb[16]
                //  -- end of SCSI_PASS_THROUGH_DIRECT at 56 --
                //  56: UCHAR  SenseBuf[32]
                //  -- total: 88 bytes --
                const int SPTD_SIZE = 56;       // sizeof(SCSI_PASS_THROUGH_DIRECT) on x64
                const int SENSE_SIZE = 32;
                const int TOTAL_SIZE = SPTD_SIZE + SENSE_SIZE; // 88

                var ptr = Marshal.AllocHGlobal(TOTAL_SIZE);
                try
                {
                    // Zero the entire buffer
                    for (int i = 0; i < TOTAL_SIZE; i++)
                        Marshal.WriteByte(ptr, i, 0);

                    // Fill SCSI_PASS_THROUGH_DIRECT fields
                    Marshal.WriteInt16(ptr, 0, (short)SPTD_SIZE);                    // Length
                    int cdbLen = Math.Min(cdb.Length, 16);
                    Marshal.WriteByte(ptr, 6, (byte)cdbLen);                         // CdbLength
                    Marshal.WriteByte(ptr, 7, SENSE_SIZE);                           // SenseInfoLength
                    Marshal.WriteByte(ptr, 8, direction);                            // DataIn
                    Marshal.WriteInt32(ptr, 12, data.Length);                         // DataTransferLength
                    Marshal.WriteInt32(ptr, 16, 10);                                 // TimeOutValue (seconds)
                    if (data.Length > 0)
                        Marshal.WriteIntPtr(ptr, 24, dataHandle.AddrOfPinnedObject()); // DataBuffer
                    Marshal.WriteInt32(ptr, 32, SPTD_SIZE);                          // SenseInfoOffset

                    // Copy CDB bytes at offset 36
                    Marshal.Copy(cdb, 0, ptr + 36, cdbLen);

                    if (!DeviceIoControl(_handle, IOCTL_SCSI_PASS_THROUGH_DIRECT,
                        ptr, (uint)TOTAL_SIZE,
                        ptr, (uint)TOTAL_SIZE,
                        out _, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        Logger.Warning("ScsiPanelDevice: DeviceIoControl failed (CDB={Cdb}), Win32 error {Error}",
                            BitConverter.ToString(cdb, 0, cdbLen), error);
                        return false;
                    }

                    // Check SCSI status
                    byte scsiStatus = Marshal.ReadByte(ptr, 2);
                    if (scsiStatus != 0)
                    {
                        // Log sense data for diagnostics
                        byte senseKey = (byte)(Marshal.ReadByte(ptr, SPTD_SIZE + 2) & 0x0F);
                        byte asc = Marshal.ReadByte(ptr, SPTD_SIZE + 12);
                        byte ascq = Marshal.ReadByte(ptr, SPTD_SIZE + 13);
                        Logger.Warning("ScsiPanelDevice: SCSI status 0x{Status:X2}, sense key=0x{Key:X} ASC=0x{ASC:X2} ASCQ=0x{ASCQ:X2}",
                            scsiStatus, senseKey, asc, ascq);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            finally
            {
                if (dataHandle.IsAllocated)
                    dataHandle.Free();
            }
        }

        public void Dispose()
        {
            if (_handle != null && !_handle.IsInvalid)
            {
                _handle.Dispose();
                Logger.Debug("ScsiPanelDevice: Closed {Path}", _devicePath);
            }
        }
    }
}
