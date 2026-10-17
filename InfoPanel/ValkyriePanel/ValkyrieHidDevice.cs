using HidSharp;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace InfoPanel.ValkyriePanel
{
    /// <summary>The native Windows HID driver remains attached to interface 0.</summary>
    internal sealed class ValkyrieHidDevice : IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
        private readonly HidStream _stream;
        private readonly SemaphoreSlim _gate;
        private bool _disposed;

        public ValkyrieHidDevice(HidDevice device)
        {
            if (device.GetMaxFeatureReportLength() != 9)
                throw new IOException("Unexpected MS9132 HID feature report size.");
            var serial = device.GetSerialNumber();
            if (string.IsNullOrWhiteSpace(serial)) throw new IOException("MS9132 serial number is unavailable.");
            _gate = Gates.GetOrAdd(serial, _ => new SemaphoreSlim(1, 1));
            if (!_gate.Wait(0)) throw new IOException("This V360 HID interface is already in use by InfoPanel.");
            try
            {
                _stream = device.Open();
                _stream.ReadTimeout = 1000;
                _stream.WriteTimeout = 1000;
            }
            catch { _gate.Release(); throw; }
        }

        public byte[] Command(byte[] payload, bool readReply)
        {
            if (payload.Length > 8) throw new ArgumentException("MS9132 payload exceeds 8 bytes.", nameof(payload));
            var report = new byte[9]; // Report ID 0 followed by the eight-byte command.
            payload.CopyTo(report, 1);
            _stream.SetFeature(report);
            var reply = new byte[9];
            if (readReply) _stream.GetFeature(reply);
            return reply;
        }

        public bool IsV360()
        {
            byte Read(int offset)
            {
                int address = 0xC000 + offset;
                return Command([0xB5, (byte)(address >> 8), (byte)address], true)[4];
            }
            return ValkyrieFrameProtocol.IsV360Edid(Read);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream.Dispose(); }
            finally { _gate.Release(); }
        }
    }
}
