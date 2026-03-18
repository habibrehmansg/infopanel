namespace InfoPanel.Plugins.Graphics
{
    /// <summary>
    /// Describes an image output produced by a plugin.
    /// </summary>
    public class PluginImageDescriptor
    {
        public string Id { get; }
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }

        public PluginImageDescriptor(string id, string name, int width, int height)
        {
            Id = id;
            Name = name;
            Width = width;
            Height = height;
        }
    }
}
