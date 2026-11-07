using System;
using System.Buffers.Binary;
using System.Text;

namespace InfoPanel.Panels.BeadaPanel;

/// <summary>Why an info reply was refused.</summary>
public enum BeadaPanelInfoError
{
    None,
    /// <summary>Fewer than 100 bytes.</summary>
    Short,
    /// <summary>No <c>STATUS-LINK</c> at byte 0.</summary>
    Header,
    /// <summary>Not a GetPanelInfo reply (byte 12).</summary>
    Type,
    /// <summary>A model code the table doesn't hold (code 1, or a newer model).</summary>
    UnknownModel,
    /// <summary>A stream size of zero pixels across or down.</summary>
    NoSize,
}

/// <summary>A panel's answer to GetPanelInfo.</summary>
public sealed record BeadaPanelInfo(
    ushort Firmware,
    byte PanelLinkVersion,
    byte StatusLinkVersion,
    byte Platform,
    BeadaPanelModel Model,
    string Serial,
    int ResolutionX,
    int ResolutionY,
    uint StorageKb,
    byte MaxBrightness,
    byte CurrentBrightness)
{
    /// <summary>Write-through mode: platform 1 or 2. The stream is the model's native size, else the reported one.</summary>
    public bool WriteThrough => Platform is 1 or 2;

    /// <summary>The stream's width in pixels.</summary>
    public int Width => WriteThrough ? Model.Width : ResolutionX;

    /// <summary>The stream's height in pixels.</summary>
    public int Height => WriteThrough ? Model.Height : ResolutionY;

    /// <summary>
    /// Parses a GetPanelInfo reply: at least 100 bytes, <c>STATUS-LINK</c> at 0, type 1 at 12, the payload from 20.
    /// Null, with the <paramref name="error"/> and the <paramref name="modelCode"/> read (when it got that far), otherwise;
    /// a stream of no width or height is refused too.
    /// </summary>
    public static BeadaPanelInfo? Parse(ReadOnlySpan<byte> reply, out BeadaPanelInfoError error, out byte modelCode)
    {
        modelCode = 0;
        if (reply.Length < BeadaPanelProtocol.InfoLength)
        {
            error = BeadaPanelInfoError.Short;
            return null;
        }
        if (!reply[..11].SequenceEqual("STATUS-LINK"u8))
        {
            error = BeadaPanelInfoError.Header;
            return null;
        }
        if (reply[12] != (byte)StatusLinkType.GetPanelInfo)
        {
            error = BeadaPanelInfoError.Type;
            return null;
        }
        var payload = reply.Slice(20, 80);
        modelCode = payload[5];
        var model = BeadaPanelModel.ByCode(modelCode);
        if (model is null)
        {
            error = BeadaPanelInfoError.UnknownModel;
            return null;
        }
        var info = new BeadaPanelInfo(
            Firmware: BinaryPrimitives.ReadUInt16LittleEndian(payload),
            PanelLinkVersion: payload[2],
            StatusLinkVersion: payload[3],
            Platform: payload[4],
            Model: model,
            Serial: SerialOf(payload.Slice(6, 64)),
            ResolutionX: BinaryPrimitives.ReadUInt16LittleEndian(payload[70..]),
            ResolutionY: BinaryPrimitives.ReadUInt16LittleEndian(payload[72..]),
            StorageKb: BinaryPrimitives.ReadUInt32LittleEndian(payload[74..]),
            MaxBrightness: payload[78],
            CurrentBrightness: payload[79]);
        if (info.Width <= 0 || info.Height <= 0)
        {
            error = BeadaPanelInfoError.NoSize;
            return null;
        }
        error = BeadaPanelInfoError.None;
        return info;
    }

    /// <summary>1.4's rule: ASCII, NULs then white space trimmed from both ends, empty unless letters and digits only.</summary>
    private static string SerialOf(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.ASCII.GetString(bytes).Trim('\0').Trim();
        foreach (var c in text)
            if (!char.IsAsciiLetterOrDigit(c))
                return "";
        return text;
    }
}
