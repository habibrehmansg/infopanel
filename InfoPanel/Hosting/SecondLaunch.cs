using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfoPanel.Hosting;

/// <summary>
/// A second launch: the user starts InfoPanel again on a data folder another engine holds. It asks that engine, over the
/// folder's local channel as a <c>launch</c> companion (<see cref="LocalChannel.LaunchClient"/>), to open the editor; the
/// engine opens it as the menu's Open Editor does. The launch then exits 3 as before.
/// </summary>
public static class SecondLaunch
{
    /// <summary>How long the launch waits for the running engine.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether the engine holding <paramref name="dataRoot"/> opened the editor.</summary>
    public static async Task<bool> OpenEditorAsync(string runDirectory, string dataRoot)
    {
        try
        {
            using var limit = new CancellationTokenSource(Timeout);
            var endpoint = LocalChannel.EndpointFor(runDirectory, dataRoot);
            await using var channel = await LocalChannelClient.TryConnectAsync(endpoint, Timeout, limit.Token, client: LocalChannel.LaunchClient);
            if (channel == null)
                return false;
            var answer = await channel.SendAsync(new JsonObject { ["op"] = LocalChannel.OpenEditorOp }, limit.Token);
            return answer["opened"] is JsonValue opened && opened.TryGetValue<bool>(out var yes) && yes;
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
