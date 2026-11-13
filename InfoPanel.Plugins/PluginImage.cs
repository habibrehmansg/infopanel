namespace InfoPanel.Plugins
{
    /// <summary>
    /// A picture a plugin holds (a cover, an icon, a snapshot), listed by InfoPanel 2.0 as an image sensor: give it the
    /// picture's encoded bytes, a PNG, JPEG, GIF or WebP of at most <see cref="MaxBytes"/>, as they change.
    /// <code>
    /// var cover = new PluginImage("cover", "Cover Art");
    /// container.Entries.Add(cover);
    /// cover.SetImage(pngOrJpegBytes);   // copied
    /// cover.Clear();                    // no picture: the sensor is unavailable
    /// </code>
    /// The host reads the picture once a cycle and sends it only when its bytes changed, at most once a second; a
    /// picture over <see cref="MaxBytes"/> is left out, as no picture, with one warning, and InfoPanel refuses bytes that
    /// aren't one of the four formats.
    /// </summary>
    public class PluginImage(string id, string name) : IPluginImage
    {
        /// <summary>The largest picture sent (8 MiB).</summary>
        public const int MaxBytes = 8 * 1024 * 1024;

        // The picture as last given, never changed after: a set or a clear replaces the whole array, so a read takes one
        // picture whichever thread sets it.
        private byte[]? _bytes;

        public string Id { get; } = id;

        public string Name { get; } = name;

        public PluginImage(string name) : this(IdUtil.Encode(name), name)
        {
        }

        /// <summary>The picture the entry holds now, empty when it holds none.</summary>
        public ReadOnlyMemory<byte> Value => Volatile.Read(ref _bytes);

        /// <summary>Holds a copy of <paramref name="bytes"/> as the picture; none when they are empty.</summary>
        public void SetImage(ReadOnlySpan<byte> bytes) => Volatile.Write(ref _bytes, bytes.IsEmpty ? null : bytes.ToArray());

        /// <summary>Holds no picture: the sensor is unavailable until the next <see cref="SetImage"/>.</summary>
        public void Clear() => Volatile.Write(ref _bytes, null);

        public override string ToString() => Volatile.Read(ref _bytes) is { } bytes ? $"a picture of {bytes.Length} bytes" : "no picture";
    }
}
