using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace InfoPanel.ThermalrightPanel
{
    /// <summary>
    /// Direct WinUSB API wrapper for Thermalright panels.
    /// Uses P/Invoke to bypass LibUsbDotNet issues.
    /// </summary>
    public class WinUsbDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<WinUsbDevice>();

        // Device interface GUIDs to try (in order of preference)
        private static readonly Guid GUID_DEVINTERFACE_WINUSB = new Guid("dee824ef-729b-4a0e-9c14-b7117d33a817");
        private static readonly Guid GUID_DEVINTERFACE_USB_DEVICE = new Guid("a5dcbf10-6530-11d2-901f-00c04fb951ed");
        // Additional GUIDs found on Thermalright devices
        private static readonly Guid GUID_DEVINTERFACE_ALT1 = new Guid("07ca7d66-272e-4431-820b-81f247da69ca");
        private static readonly Guid GUID_DEVINTERFACE_ALT2 = new Guid("a6782bce-4376-4de2-8096-70aa9e8fed19");

        private static readonly Guid[] DEVICE_GUIDS = {
            GUID_DEVINTERFACE_WINUSB,
            GUID_DEVINTERFACE_ALT1,
            GUID_DEVINTERFACE_ALT2,
            GUID_DEVINTERFACE_USB_DEVICE
        };

        private SafeFileHandle? _deviceHandle;
        private IntPtr _winUsbHandle;
        private bool _disposed;

        public bool IsOpen => _deviceHandle != null && !_deviceHandle.IsInvalid && !_deviceHandle.IsClosed && _winUsbHandle != IntPtr.Zero;

        #region P/Invoke Declarations

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(
            SafeFileHandle DeviceHandle,
            out IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(
            IntPtr InterfaceHandle,
            byte PipeID,
            byte[] Buffer,
            uint BufferLength,
            out uint LengthTransferred,
            IntPtr Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(
            IntPtr InterfaceHandle,
            byte PipeID,
            byte[] Buffer,
            uint BufferLength,
            out uint LengthTransferred,
            IntPtr Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(
            IntPtr InterfaceHandle,
            byte PipeID,
            uint PolicyType,
            uint ValueLength,
            ref uint Value);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid ClassGuid,
            IntPtr Enumerator,
            IntPtr hwndParent,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr DeviceInfoSet,
            IntPtr DeviceInfoData,
            ref Guid InterfaceClassGuid,
            uint MemberIndex,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize,
            out uint RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x01;
        private const uint FILE_SHARE_WRITE = 0x02;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        // Pipe policy types
        private const uint PIPE_TRANSFER_TIMEOUT = 0x03;

        #endregion

        /// <summary>
        /// Find and open the first Thermalright device using WinUSB.
        /// </summary>
        public static WinUsbDevice? Open(int vendorId, int productId, int maxRetries = 3, int retryDelayMs = 500)
        {
            var devicePath = FindDevicePath(vendorId, productId);
            if (devicePath == null)
            {
                Logger.Warning("WinUsbDevice: No device found with VID={Vid:X4} PID={Pid:X4}", vendorId, productId);
                return null;
            }

            Logger.Information("WinUsbDevice: Found device at {Path}", devicePath);

            // Try multiple times in case another app just released the device
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                Logger.Information("WinUsbDevice: Open attempt {Attempt}/{MaxAttempts}", attempt, maxRetries);

                var device = new WinUsbDevice();
                if (device.OpenDevice(devicePath))
                {
                    return device;
                }

                device.Dispose();

                if (attempt < maxRetries)
                {
                    Logger.Information("WinUsbDevice: Waiting {Delay}ms before retry...", retryDelayMs);
                    System.Threading.Thread.Sleep(retryDelayMs);
                }
            }

            Logger.Error("WinUsbDevice: Failed to open device after {MaxRetries} attempts", maxRetries);
            return null;
        }

        private static string? FindDevicePath(int vendorId, int productId)
        {
            // Try each GUID in order until we find the device
            foreach (var testGuid in DEVICE_GUIDS)
            {
                var path = FindDevicePathWithGuid(vendorId, productId, testGuid);
                if (path != null)
                {
                    return path;
                }
            }

            Logger.Warning("WinUsbDevice: Device not found with any known GUID");
            return null;
        }

        private static string? FindDevicePathWithGuid(int vendorId, int productId, Guid guid)
        {
            Logger.Debug("WinUsbDevice: Searching with GUID {Guid}", guid);

            var deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            {
                return null;
            }

            try
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = (uint)Marshal.SizeOf(interfaceData);

                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData))
                {
                    // Get required size
                    SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);

                    // Allocate buffer for detail data
                    var detailDataBuffer = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        // Set cbSize for SP_DEVICE_INTERFACE_DETAIL_DATA (varies by platform)
                        Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : 6);

                        if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, out _, IntPtr.Zero))
                        {
                            // Device path starts at offset 4
                            var devicePath = Marshal.PtrToStringUni(detailDataBuffer + 4);

                            if (devicePath != null)
                            {
                                // Check if this device matches our VID/PID
                                var vidStr = $"VID_{vendorId:X4}";
                                var pidStr = $"PID_{productId:X4}";

                                if (devicePath.ToUpperInvariant().Contains(vidStr) &&
                                    devicePath.ToUpperInvariant().Contains(pidStr))
                                {
                                    Logger.Information("WinUsbDevice: Found device via GUID {Guid}: {Path}", guid, devicePath);
                                    return devicePath;
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailDataBuffer);
                    }

                    index++;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return null;
        }

        private bool OpenDevice(string devicePath)
        {
            // Try multiple approaches to open the device
            // Approach 1: Exclusive access without sharing (how most WinUSB apps work)
            Logger.Debug("WinUsbDevice: Trying exclusive access...");
            _deviceHandle = CreateFile(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                0, // No sharing - exclusive access
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (_deviceHandle.IsInvalid)
            {
                var error1 = Marshal.GetLastWin32Error();
                Logger.Warning("WinUsbDevice: Exclusive access failed with error {Error}, trying shared access...", error1);

                // Approach 2: Shared access
                _deviceHandle = CreateFile(
                    devicePath,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);

                if (_deviceHandle.IsInvalid)
                {
                    var error2 = Marshal.GetLastWin32Error();
                    Logger.Warning("WinUsbDevice: Shared access failed with error {Error}, trying without overlapped...", error2);

                    // Approach 3: Without overlapped I/O
                    _deviceHandle = CreateFile(
                        devicePath,
                        GENERIC_READ | GENERIC_WRITE,
                        0,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        IntPtr.Zero);

                    if (_deviceHandle.IsInvalid)
                    {
                        var error3 = Marshal.GetLastWin32Error();
                        Logger.Error("WinUsbDevice: All CreateFile attempts failed. Last error: {Error}", error3);
                        Logger.Error("WinUsbDevice: Error 5 = ACCESS_DENIED. Possible causes:");
                        Logger.Error("WinUsbDevice: 1. Another application has the device open (TRCC, etc.)");
                        Logger.Error("WinUsbDevice: 2. Try running as Administrator");
                        Logger.Error("WinUsbDevice: 3. Device may need a reboot after driver change");
                        return false;
                    }
                }
            }

            Logger.Debug("WinUsbDevice: CreateFile succeeded");

            // Initialize WinUSB
            if (!WinUsb_Initialize(_deviceHandle, out _winUsbHandle))
            {
                var error = Marshal.GetLastWin32Error();
                Logger.Error("WinUsbDevice: WinUsb_Initialize failed with error {Error}", error);
                _deviceHandle.Close();
                _deviceHandle = null;
                return false;
            }

            Logger.Information("WinUsbDevice: Device opened successfully");

            // Set pipe timeout (5 seconds)
            uint timeout = 5000;
            WinUsb_SetPipePolicy(_winUsbHandle, 0x01, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);
            WinUsb_SetPipePolicy(_winUsbHandle, 0x81, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);

            return true;
        }

        /// <summary>
        /// Write data to the OUT endpoint (0x01).
        /// </summary>
        public bool Write(byte[] data, out int bytesWritten)
        {
            bytesWritten = 0;

            if (!IsOpen)
            {
                Logger.Warning("WinUsbDevice: Cannot write - device not open");
                return false;
            }

            if (!WinUsb_WritePipe(_winUsbHandle, 0x01, data, (uint)data.Length, out uint transferred, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                Logger.Error("WinUsbDevice: Write failed with error {Error}", error);
                return false;
            }

            bytesWritten = (int)transferred;
            return true;
        }

        /// <summary>
        /// Read data from the IN endpoint (0x81).
        /// </summary>
        public bool Read(byte[] buffer, out int bytesRead)
        {
            bytesRead = 0;

            if (!IsOpen)
            {
                Logger.Warning("WinUsbDevice: Cannot read - device not open");
                return false;
            }

            if (!WinUsb_ReadPipe(_winUsbHandle, 0x81, buffer, (uint)buffer.Length, out uint transferred, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                Logger.Error("WinUsbDevice: Read failed with error {Error}", error);
                return false;
            }

            bytesRead = (int)transferred;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_winUsbHandle != IntPtr.Zero)
            {
                WinUsb_Free(_winUsbHandle);
                _winUsbHandle = IntPtr.Zero;
            }

            _deviceHandle?.Dispose();
            _deviceHandle = null;
        }
    }
}
