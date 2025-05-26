using System;
using System.Buffers.Binary;
using System.Text;

namespace InfoPanel.AX206Panel
{
    public static class AX206Commands
    {
        // Constants from dpfcore4driver.c
        public const byte USBCMD_SETPROPERTY = 0x01;
        public const byte USBCMD_BLIT = 0x12;

        // USB direction flags
        public const byte DIR_IN = 0x80;  // Device to host
        public const byte DIR_OUT = 0x00; // Host to device

        // The SCSI command buffer structure used by AX206
        private static readonly byte[] SCSIHeader = new byte[]
        {
            0x55, 0x53, 0x42, 0x43, // dCBWSignature "USBC"
            0xde, 0xad, 0xbe, 0xef, // dCBWTag
            0x00, 0x80, 0x00, 0x00, // dCBWLength - Will be filled in
            0x00, // bmCBWFlags: 0x80 for data in (device to host), 0x00 for data out (host to device)
            0x00, // bCBWLUN
            0x10  // bCBWCBLength - Will be filled in
        };

        // Generate command buffer for SCSI-like commands
        public static byte[] BuildCommandBuffer(byte command, byte[] data = null, bool isDataIn = false)
        {
            int dataLength = data?.Length ?? 0;
            int cmdLength = 16; // Standard SCSI command length

            // Create the command buffer (header + command)
            var buffer = new byte[SCSIHeader.Length + cmdLength + (data != null ? data.Length : 0)];
            
            // Copy the SCSI header
            Array.Copy(SCSIHeader, 0, buffer, 0, SCSIHeader.Length);
            
            // Set direction flag
            buffer[12] = isDataIn ? DIR_IN : DIR_OUT;
            
            // Set command length
            buffer[14] = (byte)cmdLength;
            
            // Set data length
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), (uint)dataLength);
            
            // Set basic command
            buffer[15] = command;
            
            // If data is provided, copy it after the header + command
            if (data != null && data.Length > 0)
            {
                Array.Copy(data, 0, buffer, SCSIHeader.Length + cmdLength, data.Length);
            }
            
            return buffer;
        }

        // Build a blit command for updating the screen
        public static byte[] BuildBlitCommand(int x0, int y0, int x1, int y1)
        {
            byte[] cmd = new byte[16]; // SCSI command is 16 bytes
            
            cmd[6] = USBCMD_BLIT;
            cmd[7] = (byte)x0;
            cmd[8] = (byte)(x0 >> 8);
            cmd[9] = (byte)y0;
            cmd[10] = (byte)(y0 >> 8);
            cmd[11] = (byte)x1;
            cmd[12] = (byte)(x1 >> 8);
            cmd[13] = (byte)y1;
            cmd[14] = (byte)(y1 >> 8);
            
            return cmd;
        }

        // Build a set backlight command
        public static byte[] BuildSetBacklightCommand(byte brightness)
        {
            byte[] cmd = new byte[16]; // SCSI command is 16 bytes
            
            cmd[6] = USBCMD_SETPROPERTY;
            cmd[7] = 0x01; // Property: Backlight
            cmd[8] = brightness; // Brightness value (0-7)
            
            return cmd;
        }
    }
}