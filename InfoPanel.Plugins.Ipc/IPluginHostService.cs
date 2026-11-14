namespace InfoPanel.Plugins.Ipc
{
    public interface IPluginHostService
    {
        Task<List<PluginMetadataDto>> InitializeAsync();

        /// <summary>
        /// Runs the plugin's action <paramref name="methodName"/> when its state says it is enabled, without waiting for it:
        /// answered with the state it was checked against, <see cref="ActionStateDto.Running"/> true once it was started.
        /// The answer comes once the task that runs it has decided, immediately before the method: an action not enabled,
        /// or one whose call was cancelled, came too late or was overtaken by shutdown, isn't run (enabled, not running).
        /// </summary>
        Task<ActionStateDto> InvokeActionAsync(string pluginId, string methodName, CancellationToken cancellationToken);

        /// <summary>The action's state now, and whether a run of it is under way.</summary>
        Task<ActionStateDto> DescribeActionAsync(string pluginId, string methodName, CancellationToken cancellationToken);

        Task<List<PluginConfigPropertyDto>> GetConfigPropertiesAsync(string pluginId);
        Task<List<PluginConfigPropertyDto>> ApplyConfigAsync(string pluginId, string key, object? value);
        Task ShutdownAsync();
    }
}
