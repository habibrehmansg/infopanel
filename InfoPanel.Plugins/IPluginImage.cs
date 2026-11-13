namespace InfoPanel.Plugins
{
    /// <summary>
    /// An entry holding a picture's encoded bytes (a cover, an icon, a snapshot): PNG, JPEG, GIF or WebP, at most
    /// <see cref="PluginImage.MaxBytes"/>. InfoPanel 2.0 lists it as an image sensor; InfoPanel 1.x doesn't list it.
    /// </summary>
    public interface IPluginImage : IPluginData
    {
        /// <summary>
        /// The picture the entry holds now, empty when it holds none. Never changed once given: a new picture is a new
        /// value, so the host, which reads it once a cycle, can tell a new picture from the one it read before.
        /// </summary>
        ReadOnlyMemory<byte> Value { get; }
    }
}
