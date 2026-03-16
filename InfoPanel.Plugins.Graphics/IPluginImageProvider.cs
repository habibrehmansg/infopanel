namespace InfoPanel.Plugins.Graphics
{
    /// <summary>
    /// Implement this interface alongside IPlugin to declare image outputs
    /// and receive writers backed by shared memory.
    /// </summary>
    public interface IPluginImageProvider
    {
        /// <summary>
        /// Declares the image outputs this plugin produces.
        /// Called during initialization to set up shared memory buffers.
        /// </summary>
        IReadOnlyList<PluginImageDescriptor> ImageDescriptors { get; }

        /// <summary>
        /// Called by the host after shared memory buffers are created.
        /// Plugin stores the writers and draws on them during Update.
        /// </summary>
        void OnImageBuffersReady(IReadOnlyDictionary<string, IPluginImageWriter> writers);
    }
}
