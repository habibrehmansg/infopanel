using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfoPanel.Hosting;

/// <summary>
/// A launch on a data folder another engine holds (<see cref="HandOffAsync"/>). When the engine that holds it answers on
/// the folder's local channel, it is running: a second launch asks it, as a <c>launch</c> companion
/// (<see cref="LocalChannel.LaunchClient"/>), to open the editor, which it opens as the menu's Open Editor does, and the
/// launch exits 3. When nothing answers, the engine is stopping: it closes its channel before it lets go of the folder's
/// lock, which it keeps through its clean-up. The launch then waits for the folder (<see cref="HandOff"/>) and starts
/// once it is free, so a quick relaunch during a shutdown isn't turned away with the folder free a moment later.
/// </summary>
public static class SecondLaunch
{
    /// <summary>How long the launch waits for the running engine's answer, at most what is left of the hand-off.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a launch that finds the folder held, with nothing answering on its channel, waits for the folder: an
    /// engine stopping lets go of it after its channel closes; one starting opens its channel after it takes the folder.
    /// An engine too old to have the channel holds it for longer, and the launch exits 3 after this wait.
    /// </summary>
    public static readonly TimeSpan HandOff = TimeSpan.FromSeconds(5);

    /// <summary>The longest each look for the channel takes, and the pause between looks, while the launch waits.</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// What a launch that found the folder held ends with: the folder's lock when it was let go and taken; or
    /// <see cref="Opened"/> when the engine holding it opened the editor; otherwise the launch stops with
    /// <see cref="Error"/>, exit 3 while <see cref="Held"/>.
    /// </summary>
    public sealed record Outcome(InstanceLock? Lock, bool Opened, string? Error, bool Held);

    private enum Answer
    {
        None,
        Running,
        Opened,
    }

    /// <summary>
    /// A launch that found <paramref name="lockFile"/> held. While an engine answers on <paramref name="dataRoot"/>'s
    /// channel, the launch is done: that engine is asked to open the editor when <paramref name="openEditor"/>. While none
    /// does, the lock is tried again until it is taken, or <paramref name="handOff"/> has passed. The hand-off is one
    /// budget: each connection, answer and pause is cut to what is left of it.
    /// </summary>
    public static async Task<Outcome> HandOffAsync(string lockFile, string runDirectory, string dataRoot, bool openEditor, TimeSpan handOff)
    {
        var waited = Stopwatch.StartNew();
        TimeSpan Left() => handOff - waited.Elapsed;
        string? error = null;
        while (true)
        {
            switch (await AskAsync(runDirectory, dataRoot, openEditor, Left()))
            {
                case Answer.Opened:
                    return new Outcome(null, Opened: true, error, Held: true);
                case Answer.Running:
                    return new Outcome(null, Opened: false, error, Held: true);
            }

            var left = Left();
            if (left <= TimeSpan.Zero)
                return new Outcome(null, Opened: false, error, Held: true);
            await Task.Delay(left < Look ? left : Look);
            var taken = InstanceLock.TryAcquire(lockFile, out error, out var held);
            if (taken != null || !held)
                return new Outcome(taken, Opened: false, error, held);
        }
    }

    /// <summary>
    /// Whether an engine answers on <paramref name="dataRoot"/>'s channel, and when <paramref name="openEditor"/>, whether
    /// it opened the editor. A channel that refuses the launch, or a run folder that isn't trusted, counts as an engine
    /// running, as it did before the launch waited for anything: the launch stops. One that closes the connection before
    /// its hello is an engine stopping, and doesn't. The connection and the answer together take at most
    /// <paramref name="left"/>, what is left of the hand-off.
    /// </summary>
    private static async Task<Answer> AskAsync(string runDirectory, string dataRoot, bool openEditor, TimeSpan left)
    {
        if (left <= TimeSpan.Zero)
            return Answer.None;
        try
        {
            using var limit = new CancellationTokenSource(left < Timeout ? left : Timeout);
            var endpoint = LocalChannel.EndpointFor(runDirectory, dataRoot);
            await using var channel = await LocalChannelClient.TryConnectAsync(endpoint, left < Look ? left : Look, limit.Token, client: LocalChannel.LaunchClient);
            if (channel == null)
                return Answer.None;
            if (!openEditor)
                return Answer.Running;
            var answer = await channel.SendAsync(new JsonObject { ["op"] = LocalChannel.OpenEditorOp }, limit.Token);
            return answer["opened"] is JsonValue opened && opened.TryGetValue<bool>(out var yes) && yes ? Answer.Opened : Answer.Running;
        }
        catch (LocalChannelException ex) when (!ex.Closed)
        {
            return Answer.Running;
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            // The connection went as the engine stopped (closed before its hello included), or it didn't answer in time:
            // look again while the launch waits.
            return Answer.None;
        }
    }
}
