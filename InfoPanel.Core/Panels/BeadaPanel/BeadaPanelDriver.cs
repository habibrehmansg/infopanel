using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;

namespace InfoPanel.Panels.BeadaPanel;

/// <summary>
/// BeadaPanel as a panel driver, built into the engine: an adapter over <see cref="BeadaPanelSession"/>, whose protocol
/// it leaves as it is. Its probe is the info query (<see cref="BeadaPanelSession.Identify"/>): a panel reporting a model
/// the table doesn't hold is not its own; its sessions are <see cref="BeadaPanelSession.Start"/>'s, raw or JPEG as the
/// format asked for and the panel say, and their stop is the screen-off within its bound.
/// </summary>
public sealed class BeadaPanelDriver(IUsbBackend backend, TimeProvider time) : IPanelDriver
{
    /// <summary>The info reply behind each identity this driver's probes answered, for the engine's API.</summary>
    private readonly ConditionalWeakTable<PanelIdentity, BeadaPanelInfo> _infos = new();

    public string Family => SavedPanel.BeadaPanelFamily;

    public IReadOnlyList<PanelMatch> Matches { get; } = [new PanelMatch(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, BeadaPanelProtocol.Interface)];

    /// <summary>
    /// The info query, and the panel's serial: the info reply's, else the USB descriptor's when it is letters and digits
    /// only. A panel that couldn't be queried (busy, no answer, a reply refused) is asked again on a later pass.
    /// </summary>
    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        var identity = BeadaPanelSession.Identify(backend, UsbOf(device));
        if (identity.Status != UsbStatus.Ok || identity.Info is not { } info)
        {
            if (identity.Error == BeadaPanelInfoError.UnknownModel)
                return new PanelProbe(false, null, PanelDeviceStatus.Ok, $"a BeadaPanel reporting model code {identity.ModelCode}, which InfoPanel doesn't know");
            var status = StatusOf(identity.Status);
            return new PanelProbe(false, null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status,
                $"{identity.Status}{(identity.Error == BeadaPanelInfoError.None ? "" : $", {identity.Error}")}");
        }

        var serial = info.Serial.Length > 0 ? info.Serial : identity.UsbSerial is { Length: > 0 } s && s.All(char.IsAsciiLetterOrDigit) ? s : null;
        var found = new PanelIdentity(info.Model.Id, info.Width, info.Height, serial, info.Firmware.ToString());
        _infos.AddOrUpdate(found, info);
        return new PanelProbe(true, found, PanelDeviceStatus.Ok);
    }

    /// <summary>The info reply a probe of this driver read for <paramref name="identity"/>; null for any other identity.</summary>
    internal BeadaPanelInfo? InfoOf(PanelIdentity identity) => _infos.TryGetValue(identity, out var info) ? info : null;

    /// <summary>
    /// The start sequence (<see cref="BeadaPanelSession.Start"/>): a JPEG stream where the format asked for is JPEG, or
    /// where none is asked for and the panel takes one; else raw. A JPEG start tag the panel doesn't take is told to
    /// <see cref="PanelOpenOptions.FormatRefused"/> at once.
    /// </summary>
    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        var encoding = options.Format switch
        {
            null => PanelEncoding.Auto,
            PanelFrameFormat.Jpeg => PanelEncoding.Jpeg,
            _ => PanelEncoding.Raw,
        };
        var refused = options.FormatRefused;
        var start = BeadaPanelSession.Start(backend, UsbOf(device), options.Brightness, encoding, time, stop,
            refused == null ? null : status => refused(StatusOf(status)));
        return start.Session is { } session
            ? new PanelOpen(new Session(session, time), PanelDeviceStatus.Ok, start.Message)
            : new PanelOpen(null, StatusOf(start.Status), start.Message);
    }

    internal static UsbDeviceId UsbOf(PanelCandidate device) => new(device.VendorId, device.ProductId, device.Bus, device.Address, device.PortPath);

    internal static PanelDeviceStatus StatusOf(UsbStatus status) => status switch
    {
        UsbStatus.Ok => PanelDeviceStatus.Ok,
        UsbStatus.Busy => PanelDeviceStatus.Busy,
        UsbStatus.Access => PanelDeviceStatus.Access,
        UsbStatus.NoDevice => PanelDeviceStatus.Gone,
        UsbStatus.NotSupported => PanelDeviceStatus.NotSupported,
        UsbStatus.Timeout => PanelDeviceStatus.Timeout,
        _ => PanelDeviceStatus.Io,
    };

    /// <summary>
    /// A running BeadaPanel stream: its one format (JPEG or raw RGB565), its ceiling (<see cref="PanelPacer.CeilingOf"/>;
    /// 60 where it has none), JPEG at <see cref="BeadaPanelProtocol.JpegQuality"/>, the device time of a frame the time of
    /// its bulk write.
    /// </summary>
    private sealed class Session(BeadaPanelSession session, TimeProvider time) : IPanelSession
    {
        public PanelCapabilities Capabilities { get; } = new(session.Width, session.Height,
            [session.Jpeg ? PanelFrameFormat.Jpeg : PanelFrameFormat.Rgb565Le],
            MaxFps: PanelPacer.CeilingOf(session.Info, session.Jpeg) ?? 60,
            JpegQuality: BeadaPanelProtocol.JpegQuality,
            Brightness: new PanelRange(BeadaPanelProtocol.BrightnessByte(0, session.Info.PanelLinkVersion), BeadaPanelProtocol.BrightnessByte(100, session.Info.PanelLinkVersion)));

        public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
        {
            var started = time.GetTimestamp();
            var status = session.WriteFrame(frame.Bytes);
            return new PanelWrite(StatusOf(status), time.GetElapsedTime(started));
        }

        public PanelDeviceStatus SetBrightness(int percent) => StatusOf(session.SetBrightness(percent));

        /// <summary>
        /// The screen-off (<see cref="BeadaPanelSession.Stop"/>), its <see cref="BeadaPanelSession.ScreenOffBound"/> counted
        /// so that what is left of it is <paramref name="budget"/>; without <paramref name="screenOff"/>, the panel let go.
        /// </summary>
        public void Close(bool screenOff, TimeSpan budget)
        {
            if (!screenOff)
            {
                session.Dispose();
                return;
            }
            var bound = BeadaPanelSession.ScreenOffBound;
            var spent = budget <= TimeSpan.Zero ? bound : budget >= bound ? TimeSpan.Zero : bound - budget;
            session.Stop(time.GetTimestamp() - spent.Ticks * time.TimestampFrequency / TimeSpan.TicksPerSecond);
        }

        public void Dispose() => session.Dispose();
    }
}
