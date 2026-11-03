using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.SensorHelper;

/// <summary>Why the helper stopped serving.</summary>
public enum HelperExit
{
    /// <summary>The engine closed the pipe.</summary>
    PipeClosed,

    /// <summary>The engine's process ended.</summary>
    EngineExited,

    /// <summary>The helper refused what it was sent and closed the pipe.</summary>
    Refused,

    /// <summary>LibreHardwareMonitor's computer failed to open; the helper answered <c>treeFailed</c> and closed the pipe.</summary>
    TreeFailed,

    /// <summary>No engine connected within the time the helper waits for one (a task's helper: <see cref="HelperTask.ConnectWithin"/>).</summary>
    NoEngine,
}

/// <summary>
/// The helper's serve loop: one engine on one pipe, one request at a time, over a <see cref="ISensorTree"/>.
/// <c>hello</c> comes first, once, at <see cref="HelperProtocol.Version"/>; then <c>catalogue</c>, <c>sample</c> and
/// <c>configure</c> in any order. A request it refuses (before <c>hello</c>, another version, a message the protocol
/// doesn't have, JSON too deep) is answered with an <c>error</c>, then the pipe is closed; a frame it can't read
/// (cut short, empty, over the cap) closes it unanswered, and so does a frame sent before the last one's answer. A
/// read that throws is answered with <c>treeFailed</c>, and an answer over the caps with <c>tooLarge</c>; the pipe
/// stays open for both. A computer that fails to open is answered with <c>treeFailed</c> too, then the pipe is closed
/// (<see cref="HelperExit.TreeFailed"/>): the helper never opens a second one.
/// </summary>
/// <remarks>
/// The pipe is read all the time, also while the tree works, so the engine closing it is seen at once even when a call
/// into LibreHardwareMonitor never returns: the loop then returns and leaves that call behind (its process ends, which
/// is what frees it). Whoever ends the helper asks <see cref="Retire"/> first, and closes the tree only when no call is
/// inside it.
/// </remarks>
public sealed class HelperServer(ISensorTree tree, string helperVersion)
{
    private readonly object _calls = new();
    private bool _inTree;
    private bool _retired;
    private bool _greeted;
    private readonly TaskCompletionSource _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _open;
    private bool _reopened;
    private bool _storage = HelperProtocol.DefaultStorage;
    private int _storageInterval = HelperProtocol.DefaultStorageInterval;
    private long _version;
    private IReadOnlyList<HelperHardware> _hardware = [];
    private IReadOnlyList<HelperSensor> _sensors = [];

    /// <summary>Completes once the engine's hello has been taken (before its answer is written).</summary>
    public Task Greeted => _hello.Task;

    /// <summary>
    /// Stops the server calling the tree from now on, and says whether no call is inside it: only then may the tree be
    /// closed, since closing LibreHardwareMonitor's computer under a call that hangs in it could hang too, or worse.
    /// </summary>
    public bool Retire()
    {
        lock (_calls)
        {
            _retired = true;
            return !_inTree;
        }
    }

    /// <summary>
    /// Serves the engine on <paramref name="pipe"/> until it closes the pipe, the helper refuses it, or its computer
    /// fails to open.
    /// </summary>
    public async Task<HelperExit> ServeAsync(Stream pipe, CancellationToken cancel)
    {
        var next = ReadAsync(pipe, cancel);
        while (true)
        {
            var (frame, closed) = await next.ConfigureAwait(false);
            if (frame is null)
                return closed;

            // The next frame is read while this one is answered, so the pipe closing is seen however long the tree takes.
            next = ReadAsync(pipe, cancel);
            var work = Task.Run(() => Respond(frame), CancellationToken.None);
            await Task.WhenAny(work, next).ConfigureAwait(false);
            if (!work.IsCompleted)
            {
                // The engine closed the pipe, or sent another frame before this one's answer: either way the helper is
                // done, and the call still inside the tree is left behind.
                var early = await next.ConfigureAwait(false);
                return early.Frame is null ? early.Closed : HelperExit.Refused;
            }

            var (bytes, then) = await work.ConfigureAwait(false);
            try
            {
                await HelperFraming.WriteAsync(pipe, bytes, cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                return HelperExit.PipeClosed;
            }

            if (then is { } exit)
                return exit;
        }
    }

    /// <summary>
    /// The next frame, or null and why there is none: the engine closed the pipe (also when the read is cancelled or the
    /// pipe disposed under it), or the stream is no longer framed (cut short, empty, over the cap). Never throws.
    /// </summary>
    private static async Task<(byte[]? Frame, HelperExit Closed)> ReadAsync(Stream pipe, CancellationToken cancel)
    {
        try
        {
            return (await HelperFraming.ReadAsync(pipe, cancel).ConfigureAwait(false), HelperExit.PipeClosed);
        }
        catch (HelperProtocolException)
        {
            return (null, HelperExit.Refused); // nothing more can be read from it
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return (null, HelperExit.PipeClosed);
        }
    }

    /// <summary>The answer to <paramref name="frame"/> as bytes, and how serving ends after it (null: it goes on).</summary>
    private (byte[] Bytes, HelperExit? Then) Respond(byte[] frame)
    {
        HelperAnswer answer;
        HelperExit? then;
        try
        {
            (answer, then) = Answer(HelperCodec.DecodeRequest(frame));
        }
        catch (HelperProtocolException refused)
        {
            (answer, then) = (new ErrorAnswer(refused.Code, refused.Message), HelperExit.Refused);
        }

        try
        {
            return (HelperCodec.EncodeAnswer(answer), then);
        }
        catch (HelperProtocolException tooLarge)
        {
            return (HelperCodec.EncodeAnswer(new ErrorAnswer(tooLarge.Code, tooLarge.Message)), then);
        }
    }

    private (HelperAnswer Answer, HelperExit? Then) Answer(HelperRequest request)
    {
        if (request is HelloRequest hello)
        {
            if (_greeted)
                return (new ErrorAnswer("unexpected", "hello comes once."), HelperExit.Refused);
            if (hello.Protocol != HelperProtocol.Version)
                return (new ErrorAnswer("protocol", $"The helper speaks protocol {HelperProtocol.Version}."), HelperExit.Refused);

            _greeted = true;
            _hello.TrySetResult();
            return (new HelloAnswer(HelperProtocol.Version, helperVersion, tree.Elevated, tree.Driver), null);
        }

        if (!_greeted)
            return (new ErrorAnswer("unexpected", "hello comes first."), HelperExit.Refused);

        try
        {
            switch (request)
            {
                case ConfigureRequest configure:
                    (_storage, _storageInterval) = (configure.Storage, configure.StorageInterval);
                    Open();
                    return (new ConfigureAnswer(_storage, _storageInterval), null);
                case CatalogueRequest:
                    Read();
                    return (new CatalogueAnswer(_version, _hardware, _sensors), null);
                case SampleRequest:
                    var sources = Read();
                    return (new SampleAnswer(_version, [.. sources.Select(s => Finite(s.Value))], [.. sources.Select(s => Finite(s.Min))], [.. sources.Select(s => Finite(s.Max))]), null);
                default:
                    return (new ErrorAnswer("unexpected", "No such request."), HelperExit.Refused);
            }
        }
        catch (OpenFailedException failed)
        {
            return (new ErrorAnswer(ErrorAnswer.TreeFailed, $"LibreHardwareMonitor failed to open: {failed.InnerException!.GetType().Name}."), HelperExit.TreeFailed);
        }
        catch (Exception e) when (e is not HelperProtocolException)
        {
            return (new ErrorAnswer(ErrorAnswer.TreeFailed, $"LibreHardwareMonitor failed: {e.GetType().Name}."), null);
        }
    }

    /// <summary>
    /// Opens the computer (again) with the settings last given. One that fails is never tried again in this process
    /// (<see cref="OpenFailedException"/>): what it opened part way can't be closed reliably.
    /// </summary>
    private void Open()
    {
        _open = false;
        try
        {
            InTree(() => tree.Configure(_storage, _storageInterval));
        }
        catch (Exception e) when (e is not RetiredException)
        {
            throw new OpenFailedException(e);
        }

        (_open, _reopened) = (true, true);
    }

    /// <summary>Calls the tree, unless the server is retired; <see cref="Retire"/> sees the call while it is inside.</summary>
    private T InTree<T>(Func<T> call)
    {
        lock (_calls)
        {
            if (_retired)
                throw new RetiredException();
            _inTree = true;
        }

        try
        {
            return call();
        }
        finally
        {
            lock (_calls)
                _inTree = false;
        }
    }

    private void InTree(Action call) => InTree(() =>
    {
        call();
        return true;
    });

    private sealed class OpenFailedException(Exception inner) : Exception("The computer failed to open.", inner);

    private sealed class RetiredException() : InvalidOperationException("The helper is ending; the tree is called no more.");

    /// <summary>
    /// Reads the tree (opening it first with the settings it was last given), and moves the catalogue version when the
    /// list changed or the computer was opened again since the last read: a reopen is a new catalogue even when it lists
    /// the same sensors, so the engine fetches it and never pairs a sample with a list from before.
    /// </summary>
    private IReadOnlyList<TreeSensor> Read()
    {
        if (!_open)
            Open();

        var built = BuildCatalogue(InTree(tree.Update));
        if (_reopened || _version == 0 || !built.Hardware.SequenceEqual(_hardware) || !built.Sensors.SequenceEqual(_sensors))
        {
            _version++;
            (_hardware, _sensors, _reopened) = (built.Hardware, built.Sensors, false);
        }

        return built.Sources;
    }

    private static float? Finite(float? value) => value is { } number && !float.IsNaN(number) && !float.IsInfinity(number) ? number : null;

    /// <summary>
    /// The catalogue of <paramref name="snapshot"/>: each identifier exactly as the tree gave it; the sensors in 1.4.x's
    /// order (by their own hardware's type number, then their index; the tree's order among equals); each with its
    /// type's unit. A repeated identifier keeps its first entry, a sensor on hardware the tree didn't list is left out,
    /// a parent the tree didn't list becomes none, and parents that go round in a cycle lose the one that closes it
    /// (<see cref="HelperCodec.CycleClosers"/>), so the catalogue is always one the engine takes.
    /// </summary>
    public static CatalogueBuild BuildCatalogue(TreeSnapshot snapshot)
    {
        var listed = new Dictionary<string, TreeHardware>(StringComparer.Ordinal);
        var kept = new List<TreeHardware>();
        foreach (var hardware in snapshot.Hardware)
        {
            if (!string.IsNullOrEmpty(hardware.Identifier) && listed.TryAdd(hardware.Identifier, hardware))
                kept.Add(hardware);
        }

        var hardwareList = kept
            .Select(h => new HelperHardware(h.Identifier, h.Name ?? "", h.Type ?? "",
                h.Parent is { } parent && parent != h.Identifier && listed.ContainsKey(parent) ? parent : null))
            .ToList();
        foreach (var closer in HelperCodec.CycleClosers(hardwareList.Select(h => h.Id).ToList(), hardwareList.Select(h => h.Parent).ToList()))
            hardwareList[closer] = hardwareList[closer] with { Parent = null };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sensors = snapshot.Sensors
            .Where(s => !string.IsNullOrEmpty(s.Identifier) && s.Hardware is not null && listed.ContainsKey(s.Hardware) && seen.Add(s.Identifier))
            .OrderBy(s => listed[s.Hardware].TypeOrder)
            .ThenBy(s => s.Index)
            .ToList();

        return new CatalogueBuild(
            hardwareList,
            [.. sensors.Select(s => new HelperSensor(s.Identifier, s.Name ?? "", s.Type ?? "", s.Index, HelperUnits.Of(s.Type ?? ""), s.Hardware))],
            sensors);
    }
}

/// <summary>A catalogue built from a snapshot, and for each of its sensors the snapshot's sensor it came from.</summary>
public sealed record CatalogueBuild(IReadOnlyList<HelperHardware> Hardware, IReadOnlyList<HelperSensor> Sensors, IReadOnlyList<TreeSensor> Sources);
