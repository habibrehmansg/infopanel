using InfoPanel.Spec;

namespace InfoPanel.Import;

/// <summary>
/// The limits of an import: archive bodies and their entries, the 1.x XML files, the 1.x settings, the migration
/// record and the diagnostics an answer lists. Each is an option, so tests can lower it, and each takes its value from
/// <see cref="SpecLimits"/> (spec/docs/limits.md), the limits' one home. Media limits (<c>mediaBytes</c> and the rest) and
/// the document limits come from the workspace's <c>SpecLimits</c>.
/// </summary>
public sealed class ImportLimits
{
    public static ImportLimits Default { get; } = new();

    /// <summary>The largest archive file, of either kind (<c>archive.expanded</c> over it, before it is read).</summary>
    public long ArchiveBytes { get; init; } = SpecLimits.Default.ArchiveBytes;

    /// <summary>The most entries an archive may have (<c>archive.entries</c>).</summary>
    public int Entries { get; init; } = SpecLimits.Default.ArchiveEntries;

    /// <summary>
    /// The most bytes one request decompresses or reads, counted as they are read (entry headers aren't trusted); reads
    /// of a 1.x folder's assets count too (<c>archive.expanded</c>).
    /// </summary>
    public long ExpandedBytes { get; init; } = SpecLimits.Default.ExpandedBytes;

    /// <summary>The longest entry name, in UTF-8 bytes (<c>archive.name</c>).</summary>
    public int EntryNameBytes { get; init; } = SpecLimits.Default.EntryNameBytes;

    /// <summary>
    /// The largest central directory record of an entry (<c>archive.entries</c>), checked on every record before the
    /// directory is read, and times <see cref="Entries"/> on the declared directory size.
    /// </summary>
    public int EntryRecordBytes { get; init; } = SpecLimits.Default.EntryRecordBytes;

    /// <summary>The largest <c>Profile.xml</c>, <c>profiles.xml</c> or <c>settings.xml</c>.</summary>
    public int ProfileXmlBytes { get; init; } = SpecLimits.Default.V1ProfileXmlBytes;

    /// <summary>The largest items XML (<c>DisplayItems.xml</c>, <c>profiles/{guid}.xml</c>).</summary>
    public int ItemsXmlBytes { get; init; } = SpecLimits.Default.V1ItemsXmlBytes;

    /// <summary>The deepest element nesting in any 1.x XML file (the root element is at depth 1).</summary>
    public int XmlDepth { get; init; } = SpecLimits.Default.V1XmlDepth;

    /// <summary>The most elements in any 1.x XML file.</summary>
    public int XmlElements { get; init; } = SpecLimits.Default.V1XmlElements;

    /// <summary>The most profiles read from <c>profiles.xml</c>; the rest are left out with a warning.</summary>
    public int Profiles { get; init; } = SpecLimits.Default.V1Profiles;

    /// <summary>The most devices read per device family of <c>settings.xml</c>.</summary>
    public int DevicesPerFamily { get; init; } = SpecLimits.Default.V1Devices;

    /// <summary>The most fields read per device.</summary>
    public int FieldsPerDevice { get; init; } = SpecLimits.Default.V1DeviceFields;

    /// <summary>The longest settings value read, in characters.</summary>
    public int SettingValueLength { get; init; } = SpecLimits.Default.V1SettingLength;

    /// <summary>The most hotkeys read.</summary>
    public int Hotkeys { get; init; } = SpecLimits.Default.V1Hotkeys;

    /// <summary>The most profile outcomes in the migration record.</summary>
    public int RecordOutcomes { get; init; } = SpecLimits.Default.RecordOutcomes;

    /// <summary>
    /// The largest migration record, in bytes as stored: room for <see cref="RecordOutcomes"/> outcomes with ids and
    /// reasons (an outcome with both ids and no reason is about 115 bytes).
    /// </summary>
    public int RecordBytes { get; init; } = SpecLimits.Default.RecordBytes;

    /// <summary>The longest reason of a profile outcome in the record, in characters.</summary>
    public int RecordReasonLength { get; init; } = SpecLimits.Default.RecordReasonLength;

    /// <summary>The most diagnostics one answer lists; one last <c>more</c> diagnostic counts the rest.</summary>
    public int Diagnostics { get; init; } = SpecLimits.Default.Diagnostics;
}
