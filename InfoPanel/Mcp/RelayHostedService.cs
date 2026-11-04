using System.Net.NetworkInformation;
using InfoPanel.Service;
using InfoPanel.Settings;

namespace InfoPanel.Mcp;

/// <summary>
/// Runs the relay client for the engine's run (docs/2.0/relay.md): it follows the <c>service.aiApps</c> setting and every
/// settings change (an address changed, the setting turned off or on), and the computer's network changes, and stops it,
/// closing the connection as "going away", when the engine stops.
/// </summary>
public sealed class RelayHostedService(RelayClient relay, SettingsStore settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += OnSettings;
        NetworkChange.NetworkAddressChanged += OnNetwork;
        relay.SetEnabled(settings.Current.Settings.Service.AiApps);
        relay.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettings;
        NetworkChange.NetworkAddressChanged -= OnNetwork;
        await relay.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSettings(SettingsSnapshot snapshot)
    {
        relay.SetEnabled(snapshot.Settings.Service.AiApps);
        relay.ConfigChanged();
    }

    private void OnNetwork(object? sender, EventArgs e) => relay.NetworkChanged();
}
