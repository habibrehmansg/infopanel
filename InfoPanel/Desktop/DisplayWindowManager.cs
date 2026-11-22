using System.Text.Json;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Output;
using InfoPanel.Settings;
using InfoPanel.Spec;

namespace InfoPanel.Desktop;

/// <summary>
/// Shows every profile whose <c>window.enabled</c> is true as a borderless desktop window (1.x's <c>DisplayWindow</c>),
/// drawn by the profile's render loop at scale 1 through a <see cref="DesktopWindowSink"/>.
/// <list type="bullet">
/// <item>Follows the <see cref="Workspace"/>: profiles added, deleted and listed (<see cref="Workspace.ProfilesChanged"/>)
/// and every document's <see cref="ProfileDocument.Changed"/> (commits, undo, redo, revert). Every profile that can be
/// opened is opened so its changes are seen; the settings are read from its <see cref="ProfileDocument.Current"/>
/// generation.</item>
/// <item>Applies the profile's <c>width</c> and <c>height</c> and its <c>window</c> (<c>enabled</c>, <c>level</c>,
/// <c>draggable</c>, <c>resizable</c>, <c>x</c>, <c>y</c>, <c>display</c>, <c>spots</c>, <c>strict</c>) as they change, and
/// re-matches displays when they change (<see cref="DisplayMatcher.Place"/>: the most recent place whose display is
/// connected). Its own moves never produce commits.</item>
/// <item>The window's own changes are <c>profile</c> commits with a <c>gesture:</c> coalescing key of their own and the
/// revision they read as <c>baseRev</c> (undoable; read again and retried on a conflict; saved at once when it was the
/// profile's only change since it was saved, else left unsaved with the others: <see cref="SaveIfOnlyChange"/>; one
/// made while the button is still down, at the release, once for the press), each
/// putting the window on the display it ended on (<see cref="DisplayMatcher.Promote"/>: that display becomes
/// <c>display</c>, the previous one goes first in <c>spots</c>): one per finished drag (the display holding the window's
/// top-left), one per resize at its release or after <see cref="ResizeQuiet"/> without changes, whichever comes first
/// (<c>width</c>, <c>height</c>, and the display holding the window's top-left at the resize's last change, with
/// <c>window.x</c>/<c>y</c> relative to it).
/// Only the user's: never a frame left on no display, and never the system moving a window while the displays change
/// (a pending resize dropped, a same-size "resize" ignored, and a release where it left the window no drag, for
/// <see cref="DisplaySettle"/>); then the window goes back
/// to its places, hidden while none of them is connected (strict).</item>
/// <item>Every window is drawn at the engine's frame rate setting (<see cref="SettingsStore"/>); a change re-times every
/// window's render loop at once, without reattaching it.</item>
/// <item>All its state lives on the platform's UI thread: every event is posted there (<see cref="IDesktopPlatform.Post"/>).</item>
/// </list>
/// Nothing happens with <c>--no-windows</c>; on a platform that can't show windows it logs
/// <see cref="IDesktopPlatform.UnsupportedMessage"/> once if a profile is active, and does nothing else.
/// </summary>
public sealed class DisplayWindowManager(
    IDesktopPlatform platform,
    Workspace workspace,
    RenderService render,
    SettingsStore settings,
    EngineOptions options,
    TimeProvider time,
    ILogger<DisplayWindowManager> logger) : IHostedService
{
    /// <summary>
    /// How long a window's size must stay unchanged before a resize becomes an edit when the button isn't released first
    /// (1.x: 300 ms); the release commits it at once.
    /// </summary>
    public static readonly TimeSpan ResizeQuiet = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long after the platform last said the displays changed they are taken as still changing (the system moving
    /// windows off and onto them; Windows' platform says it once more after a second). Then every window is placed
    /// again by its profile.
    /// </summary>
    public static readonly TimeSpan DisplaySettle = TimeSpan.FromSeconds(2);

    /// <summary>How many times a drag's or resize's commit is tried, read again after each conflict.</summary>
    internal const int CommitAttempts = 3;

    /// <summary>Tests: called with the profile's id after a drag or resize read the profile, before it commits.</summary>
    internal Action<string>? BeforeCommit;

    private readonly Dictionary<string, ProfileDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WindowEntry> _windows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unopened = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reduced = new(StringComparer.Ordinal);

    /// <summary>The random part of this manager's coalescing keys (spec: <c>gesture:{client}:{n}</c>).</summary>
    private readonly string _gestureClient = Guid.NewGuid().ToString("N");
    private int _gestures;
    private IReadOnlyList<DisplayInfo>? _displays;
    /// <summary>Until when the displays are taken as still changing, and the timer that places every window again then (UI thread).</summary>
    private DateTimeOffset _settleUntil = DateTimeOffset.MinValue;
    private ITimer? _settleTimer;
    /// <summary>The frame rate the windows' loops were last told of (UI thread).</summary>
    private int _frameRate;
    private bool _running;
    private bool _stopped;

    /// <summary>True when the manager shows windows (not <c>--no-windows</c>, and the platform can).</summary>
    public bool Enabled => !options.NoWindows && platform.IsSupported;

    /// <summary>The profiles that have a window (UI thread; for tests).</summary>
    public IReadOnlyCollection<string> WindowProfiles => _windows.Keys;

    /// <summary>The profile's window, or null (UI thread; for tests).</summary>
    public IDesktopWindow? WindowOf(string profileId) => _windows.GetValueOrDefault(profileId)?.Window;

    /// <summary>How many profile ids the warnings once-per-profile remember: unopened, reduced resolution (UI thread; for tests).</summary>
    internal (int Unopened, int Reduced) Warned => (_unopened.Count, _reduced.Count);

    /// <summary>The profile's frame sink, or null (UI thread; for tests).</summary>
    public DesktopWindowSink? SinkOf(string profileId) => _windows.GetValueOrDefault(profileId)?.Sink;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.NoWindows)
            return Task.CompletedTask;

        if (!platform.IsSupported)
        {
            LogUnsupported();
            return Task.CompletedTask;
        }

        _running = true;
        _frameRate = FrameRate();
        workspace.ProfilesChanged += OnProfilesChanged;
        platform.DisplaysChanged += OnDisplaysChanged;
        settings.Changed += OnSettingsChanged;
        platform.Post(SyncProfiles);
        return Task.CompletedTask;
    }

    /// <summary>Closes every window (on the UI thread) and waits for it, or for <paramref name="cancellationToken"/>.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_running)
            return;

        _running = false;
        workspace.ProfilesChanged -= OnProfilesChanged;
        platform.DisplaysChanged -= OnDisplaysChanged;
        settings.Changed -= OnSettingsChanged;

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        platform.Post(() =>
        {
            try
            {
                _stopped = true;
                _settleTimer?.Dispose();
                foreach (var id in _windows.Keys.ToList())
                    CloseWindow(id);
                foreach (var (_, doc) in _documents)
                    doc.Changed -= OnDocumentChanged;
                _documents.Clear();
                _unopened.Clear();
                _reduced.Clear();
            }
            finally
            {
                closed.TrySetResult();
            }
        });

        try
        {
            await closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Desktop windows didn't close before shutdown");
        }
    }

    /// <summary>The frames a second every window asks for: the frame rate setting.</summary>
    private int FrameRate() => settings.Current.Settings.FrameRate;

    /// <summary>Any thread (the settings' lock held): when the frame rate changed, re-times every window's loop, on the UI thread.</summary>
    private void OnSettingsChanged(SettingsSnapshot snapshot)
    {
        var fps = snapshot.Settings.FrameRate;
        platform.Post(() =>
        {
            if (_stopped || fps == _frameRate)
                return;
            _frameRate = fps;
            foreach (var entry in _windows.Values)
            {
                if (entry.Sink is { } sink)
                    render.RequestChanged(sink);
            }
        });
    }

    /// <summary>Says once that windows can't be shown here, when a profile asks for one.</summary>
    private void LogUnsupported()
    {
        if (platform.UnsupportedMessage is not { } message)
            return;

        if (workspace.List().Any(e => e.Status == "ok" && workspace.Open(e.Id)?.Current.Document.Profile.Window?.Enabled == true))
            logger.LogInformation("{Message}", message);
    }

    private void OnProfilesChanged(object? sender, ProfilesChangedEventArgs e) => platform.Post(SyncProfiles);

    /// <summary>
    /// Raised on the UI thread. When the displays listed differ from those the windows were last placed by, every pending
    /// resize is dropped here, at once: the system moves windows as displays come and go, and macOS reports such a move
    /// as a resize, which can't be told from the user's; a resize commit already posted would otherwise run before the
    /// posted <see cref="RematchDisplays"/> and make a display the window's place. The displays are then taken as still
    /// changing for <see cref="DisplaySettle"/>, after which every window is placed again by its profile (the system may
    /// have moved one after <see cref="RematchDisplays"/>, saying nothing). Said with the same displays listed (Windows
    /// says it once more a second after a change), it is a refresh: the windows are matched again, nothing dropped.
    /// </summary>
    private void OnDisplaysChanged()
    {
        if (!_stopped && (_displays is not { } placed || !placed.SequenceEqual(platform.GetDisplays())))
        {
            foreach (var entry in _windows.Values.Where(e => e.ResizePending))
                DropResize(entry);
            _settleUntil = time.GetUtcNow() + DisplaySettle;
            _settleTimer?.Dispose();
            _settleTimer = time.CreateTimer(_ => platform.Post(() =>
            {
                if (!_stopped)
                    RematchDisplays();
            }), null, DisplaySettle, Timeout.InfiniteTimeSpan);
        }

        platform.Post(RematchDisplays);
    }

    /// <summary>
    /// UI thread: the displays are still changing: the platform said so less than <see cref="DisplaySettle"/> ago, or
    /// already lists others than the windows were last placed by (it hasn't said so yet).
    /// </summary>
    private bool DisplaysSettling(IReadOnlyList<DisplayInfo> displays) =>
        time.GetUtcNow() < _settleUntil || _displays is { } placed && !placed.SequenceEqual(displays);

    /// <summary>UI thread: ends, with no edit, each pending resize whose display isn't among <paramref name="displays"/> as it was.</summary>
    private void DropResizesOff(IReadOnlyList<DisplayInfo> displays)
    {
        foreach (var entry in _windows.Values.Where(e => e.ResizePending && e.ResizeDisplay is { } d && !displays.Contains(d)))
            DropResize(entry);
    }

    /// <summary>Ends the window's pending resize with no edit, its commit, posted or not, included.</summary>
    private static void DropResize(WindowEntry entry)
    {
        entry.ResizeTimer?.Dispose();
        entry.ResizeTimer = null;
        entry.ResizePending = false;
        entry.ResizeGeneration++;
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (e.Change is not (DocumentChange.Saved or DocumentChange.History) && sender is ProfileDocument doc)
            platform.Post(() => Reconcile(doc.Id, edited: true));
    }

    /// <summary>UI thread: follows the profile list (opening new profiles, dropping deleted ones), then reconciles every window.</summary>
    private void SyncProfiles()
    {
        if (_stopped)
            return;

        _displays ??= platform.GetDisplays();
        var listed = workspace.List();
        var ids = listed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        // A deleted profile's warnings are forgotten (an id created again is warned about again).
        _unopened.IntersectWith(ids);
        _reduced.IntersectWith(ids);
        foreach (var (id, doc) in _documents.ToList())
        {
            if (ids.Contains(id) && workspace.IsOpen(doc))
                continue;

            doc.Changed -= OnDocumentChanged;
            _documents.Remove(id);
            CloseWindow(id);
        }

        foreach (var entry in listed)
        {
            if (_documents.ContainsKey(entry.Id))
                continue;

            if (workspace.Open(entry.Id) is not { } doc)
            {
                if (_unopened.Add(entry.Id))
                    logger.LogWarning("Profile {ProfileId} is {Status}; it gets no desktop window", entry.Id, entry.Status);
                continue;
            }

            doc.Changed += OnDocumentChanged;
            _documents[entry.Id] = doc;
        }

        ReconcileAll();
    }

    /// <summary>
    /// UI thread: re-matches every window. With a display gone or changed the system may have moved a window the user
    /// is holding a press on, out of its way: that press's release is then no move (<see cref="OnUserMoved"/>). A
    /// pending resize whose display went away or changed is dropped the same way (already in
    /// <see cref="OnDisplaysChanged"/>), its posted commit included: it never makes that display the window's place.
    /// </summary>
    private void RematchDisplays()
    {
        var displays = platform.GetDisplays();
        if (_displays == null || _displays.Except(displays).Any())
        {
            foreach (var entry in _windows.Values.Where(e => e.Window.UserMoving))
                entry.DisplaysChanged = true;
        }

        DropResizesOff(displays);
        _displays = displays;
        ReconcileAll();
    }

    private void ReconcileAll()
    {
        foreach (var id in _documents.Keys.ToList())
            Reconcile(id);
    }

    /// <summary>
    /// UI thread: makes the profile's window match the profile (open or closed, style, place). An
    /// <paramref name="edited"/> profile whose window settings, width and height are those it was last placed by (an
    /// item's edit) gets its style (an unchanged style costs nothing) and isn't placed again: displays, scales and the
    /// user's moves reconcile without it.
    /// </summary>
    private void Reconcile(string id, bool edited = false)
    {
        if (_stopped || !_documents.TryGetValue(id, out var doc))
            return;

        var settings = Settings.Read(doc);
        if (!settings.Active)
        {
            CloseWindow(id);
            return;
        }

        if (!_windows.TryGetValue(id, out var entry))
            entry = OpenWindow(id, doc, settings);
        else if (entry.Window.Style.ClickThrough != settings.Style.ClickThrough)
            logger.LogInformation(settings.Style.ClickThrough
                ? "The desktop window of profile {ProfileId} lets clicks through"
                : "The desktop window of profile {ProfileId} takes clicks again", id);

        entry.Window.SetStyle(settings.Style);
        if (entry.ResizePending || (edited && entry.Placed == settings))
            return;

        Place(entry, settings);
        entry.Placed = settings;
    }

    private WindowEntry OpenWindow(string id, ProfileDocument doc, Settings settings)
    {
        var displays = platform.GetDisplays();
        var scale = displays.FirstOrDefault()?.Scale ?? 1;
        var window = platform.CreateWindow(settings.Style, new PointRect(0, 0, settings.Width / scale, settings.Height / scale));
        var entry = new WindowEntry(id, doc, window);
        window.UserMoved += () => OnUserMoved(entry);
        window.UserResized += () => OnUserResized(entry);
        window.BackingScaleChanged += () => OnBackingScaleChanged(entry);
        _windows[id] = entry;
        AttachSink(entry);

        if (settings.Style.ClickThrough)
            logger.LogInformation("Showing profile {ProfileId} as a desktop window, clicks passing through it", id);
        else
            logger.LogInformation("Showing profile {ProfileId} as a desktop window", id);
        return entry;
    }

    /// <summary>Gives the window a new sink on the profile's render loop (a retired sink is never attached again).</summary>
    private void AttachSink(WindowEntry entry)
    {
        DesktopWindowSink sink = null!;
        sink = new DesktopWindowSink(entry.ProfileId, platform, FrameRate, frame => ShowFrame(entry, frame), reason => OnSinkClosed(entry, sink, reason));
        entry.Sink = sink;
        try
        {
            if (!render.Attach(sink, entry.Document))
                entry.Sink = null;
        }
        catch (ObjectDisposedException ex)
        {
            logger.LogWarning(ex, "Profile {ProfileId} can't be rendered to its desktop window", entry.ProfileId);
            entry.Sink = null;
        }
    }

    /// <summary>The window gets no more frames: its sink retired (a pending frame dropped) and detached, so a loop with no other output stops.</summary>
    private void DetachSink(WindowEntry entry)
    {
        if (entry.Sink is not { } sink)
            return;

        entry.Sink = null;
        sink.Retire();
        render.Detach(sink);
    }

    private void CloseWindow(string id)
    {
        if (!_windows.Remove(id, out var entry))
            return;

        entry.Closed = true;
        entry.ResizeTimer?.Dispose();
        DetachSink(entry);

        entry.Window.Dispose();
        logger.LogInformation("Closed the desktop window of profile {ProfileId}", id);
    }

    /// <summary>
    /// Puts the window at its place (<see cref="DisplayMatcher.Place"/>), or hides it when none of its displays is
    /// connected (strict). During a drag it stays where the user has it: placing it then (a scale or display change, any
    /// other profile's sync) would move it from under the mouse, back to where the profile has it. The release
    /// (<see cref="OnUserMoved"/>) places it. Hidden, it keeps its last picture but gets no frames (its sink is detached);
    /// shown again, a new sink gets the loop's next frame.
    /// </summary>
    private void Place(WindowEntry entry, Settings settings)
    {
        var window = entry.Window;
        var displays = platform.GetDisplays();
        var place = DisplayMatcher.Place(displays, settings.Window, settings.Width, settings.Height);
        if (place is not { } at)
        {
            window.SetVisible(false);
            if (entry.Sink != null)
            {
                DetachSink(entry);
                entry.SinkHidden = true;
            }
            return;
        }

        if (window.UserMoving)
            return;

        var (display, x, y) = at;
        // Sized at the scale of the display that will hold the window's top-left; if the window lands on a display
        // of another scale (it is mostly on that one), sized again at the window's own scale.
        var origin = DisplayMatcher.Frame(display, x, y, 1, 1, 1);
        var sizeScale = DisplayMatcher.At(displays, origin.X, origin.Y)?.Scale ?? display.Scale;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var frame = DisplayMatcher.Frame(display, x, y, settings.Width, settings.Height, sizeScale);
            if (!DisplayMatcher.SameFrame(window.Frame, frame))
                window.SetFrame(frame);
            if (window.BackingScale == sizeScale)
                break;
            sizeScale = window.BackingScale;
        }

        entry.Settled = window.Frame;
        entry.Relocated = null;
        if (entry.SinkHidden)
        {
            entry.SinkHidden = false;
            AttachSink(entry);
        }
        window.SetVisible(true);
    }

    /// <summary>The window is sized again for its new scale (during a drag, at the release: see <see cref="Place"/>).</summary>
    private void OnBackingScaleChanged(WindowEntry entry)
    {
        if (!entry.Closed)
            Reconcile(entry.ProfileId);
    }

    /// <summary>
    /// The mouse button is up after a press on the window. Moved, the display holding the window's top-left becomes
    /// the target, the position relative to it. A click, the end of a resize, a press during which a display went
    /// away or changed, a frame left on no display (<see cref="DisplayMatcher.Holding"/>), or the window still where the
    /// system moved it while the displays settled (<see cref="WindowEntry.Relocated"/>), is no move. A resize still
    /// pending is committed here, at once, at its own last frame (not <see cref="ResizeQuiet"/> after it): the end of the
    /// resize itself, or of a drag after it, which is then an edit of its own.
    /// </summary>
    private void OnUserMoved(WindowEntry entry)
    {
        if (entry.Closed || _stopped)
            return;

        var frame = entry.Window.Frame;
        var displaysChanged = entry.DisplaysChanged;
        entry.DisplaysChanged = false;
        // Still where the system moved it (OnUserResized ignored that): the release is no drag.
        var relocated = entry.Relocated == frame;
        entry.Relocated = null;

        // A resize still pending is committed at the release, at its last frame, which becomes the settled frame: released
        // where the resize left it, the release is no move; a drag after it is an edit of its own.
        if (entry.ResizePending)
            EditResize(entry);

        if (!displaysChanged && !relocated && entry.Settled != frame && DisplayMatcher.Holding(platform.GetDisplays(), frame) is { } display)
        {
            var (x, y) = DisplayMatcher.Position(display, frame);
            Edit(entry, x, y, null, display);
        }

        SaveHeld(entry);

        // Whether or not anything changed, the window goes to exactly the profile's place, sized for its scale there.
        Reconcile(entry.ProfileId);
    }

    /// <summary>
    /// The window's frame changed under the user's resize, or so the platform says: while the displays are still
    /// changing (<see cref="DisplaysSettling"/>), a change that keeps the window's size (its pending resize's last one, or
    /// where it was placed) is the system moving it (macOS reports that as a resize) and is no edit, nor is the release
    /// that finds the window still there (<see cref="WindowEntry.Relocated"/>); the window is placed again once they
    /// settle, or when the pending resize commits, at the user's frame. A resize that leaves the frame on
    /// no display (<see cref="DisplayMatcher.Holding"/>) commits nothing and puts the window back.
    /// </summary>
    private void OnUserResized(WindowEntry entry)
    {
        if (entry.Closed || _stopped)
            return;

        var frame = entry.Window.Frame;
        var displays = platform.GetDisplays();
        var last = entry.ResizePending ? entry.ResizeFrame : entry.Settled;
        if (last is { } before && DisplayMatcher.SameSize(before, frame) && DisplaysSettling(displays))
        {
            entry.Relocated = frame;
            return;
        }

        entry.Relocated = null;
        entry.ResizePending = true;
        entry.ResizeFrame = frame;
        entry.ResizeScale = entry.Window.BackingScale;
        entry.ResizeDisplay = DisplayMatcher.Holding(displays, frame);
        var generation = ++entry.ResizeGeneration;
        entry.ResizeTimer?.Dispose();
        entry.ResizeTimer = time.CreateTimer(_ => platform.Post(() => CommitResize(entry, generation)), null, ResizeQuiet, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The size quiet for <see cref="ResizeQuiet"/>: <c>width</c>/<c>height</c> from the window's size in backing
    /// pixels, and <c>window.x</c>/<c>y</c> relative to the display that held its top-left then, which becomes its
    /// <c>display</c> (as a drag's does). A timer that fired before a later
    /// change of the same resize (its commit already posted) is stale: <paramref name="generation"/> no longer matches.
    /// </summary>
    private void CommitResize(WindowEntry entry, int generation)
    {
        if (generation != entry.ResizeGeneration)
            return;

        if (entry.Closed || _stopped || !entry.ResizePending)
            return;

        EditResize(entry);

        // Whether or not anything changed, the window goes back to exactly the profile's size and place.
        Reconcile(entry.ProfileId);
    }

    /// <summary>Ends the pending resize with its commit, from the window's frame, scale and display at the resize's last change.</summary>
    private void EditResize(WindowEntry entry)
    {
        entry.ResizeTimer?.Dispose();
        entry.ResizeTimer = null;
        entry.ResizePending = false;
        var frame = entry.ResizeFrame;
        var scale = entry.ResizeScale;
        var width = Math.Clamp((int)Math.Round(frame.Width * scale), 1, Workspace.MaxDimension);
        var height = Math.Clamp((int)Math.Round(frame.Height * scale), 1, Workspace.MaxDimension);
        if (entry.ResizeDisplay is { } display)
        {
            var (x, y) = DisplayMatcher.Position(display, frame);
            Edit(entry, x, y, (width, height), display);
        }

        entry.Settled = frame;
    }

    /// <summary>
    /// Commits the window's place, <paramref name="x"/>, <paramref name="y"/> on <paramref name="display"/>, as its
    /// <c>display</c>, the previous one remembered in <c>spots</c> (<see cref="DisplayMatcher.Promote"/>), and its size
    /// when it was resized, as one <c>profile</c> op with a <c>gesture:</c> key of its own. The op sets the whole
    /// <c>window</c> as read, so it carries that read's revision as its <c>baseRev</c>: when another change to the
    /// profile came first (409), the latest window is read and the place applied to it again, up to
    /// <see cref="CommitAttempts"/> times, so another client's <c>level</c>, <c>resizable</c>, <c>enabled</c> or
    /// <c>spots</c> is never undone. A commit that changes nothing makes no revision. One that does is saved at once
    /// when the profile had no unsaved changes just before it applied (<see cref="SaveIfOnlyChange"/>); one made while
    /// the button is still down (a resize quiet for <see cref="ResizeQuiet"/>) is saved at the release instead, once for
    /// the press, only when nothing but the press changed the profile since it was saved (<see cref="WindowEntry.Held"/>).
    /// </summary>
    private void Edit(WindowEntry entry, int x, int y, (int Width, int Height)? size, DisplayInfo display)
    {
        var doc = entry.Document;
        var key = $"gesture:{_gestureClient}:{++_gestures}";
        // A commit of the press while one waits for the release follows straight on it only when the document is still
        // at the waiting commit's revision as this one applies: seen by the guard, under the lock that applies the
        // commit, so no other client's commit can come between (a revision read before it could be passed already).
        var waiting = entry.Held;
        var follows = false;
        Action<Document>? guard = waiting is { } w ? _ => follows = doc.Rev == w.Rev : null;
        Generation current;
        CommitOutcome outcome;
        bool wasClean;
        for (var attempt = 1; ; attempt++)
        {
            current = doc.Current;
            var profile = current.Document.Profile;
            var window = DisplayMatcher.Promote(profile.Window ?? new WindowSettings { Enabled = true }, DisplayMatcher.TargetOf(display), x, y);

            BeforeCommit?.Invoke(entry.ProfileId);
            try
            {
                outcome = doc.Commit(JsonSerializer.SerializeToUtf8Bytes(new CommitRequest
                {
                    OpId = Guid.NewGuid().ToString("N"),
                    Instance = doc.Instance,
                    BaseRev = current.Rev,
                    CoalesceKey = key,
                    Ops = [new ProfileOp { BaseRev = current.Rev, Set = new ProfileFields { Window = window, Width = size?.Width, Height = size?.Height } }],
                }, SpecJson.Options), guard, out wasClean);
            }
            catch (ObjectDisposedException)
            {
                return; // deleted meanwhile; the workspace's event closes the window
            }

            if (outcome.Conflict is null && outcome.Expired is null || attempt == CommitAttempts)
                break;
        }

        if (outcome.Result is not { } result)
        {
            var reason = outcome.Invalid?.Errors.FirstOrDefault()?.Message ?? outcome.Conflict?.Detail ?? outcome.Expired?.Detail;
            logger.LogWarning("The desktop window of profile {ProfileId} couldn't update the profile ({Status}: {Reason})", entry.ProfileId, outcome.Status, reason);
            return;
        }

        if (result.Rev == current.Rev)
            return;

        if (size is { } s)
        {
            logger.LogInformation("The desktop window of profile {ProfileId} was resized on the desktop to {Width}×{Height} at ({WindowX}, {WindowY}) on {Display}",
                entry.ProfileId, s.Width, s.Height, x, y, display.Name);
        }
        else
        {
            logger.LogInformation("The desktop window of profile {ProfileId} was moved on the desktop to ({WindowX}, {WindowY}) on {Display}",
                entry.ProfileId, x, y, display.Name);
        }

        // One save per press: a commit made while the button is still down (a resize quiet for a while) waits for the
        // release, which saves the press's last commit, when the profile had no unsaved changes before its first and
        // nothing else changed it between them (each commit applied straight on the one before it, as its guard saw).
        // The first: wasClean, read under the same lock as it applied.
        var clean = waiting is { } held ? held.Clean && follows : wasClean;
        if (entry.Window.UserMoving)
        {
            entry.Held = (result.Rev, clean);
            return;
        }

        entry.Held = null;
        if (clean)
            SaveIfOnlyChange(entry, doc, result.Rev);
    }

    /// <summary>The release: a commit of the press that waits for it (<see cref="WindowEntry.Held"/>), no later commit having saved it, is saved now.</summary>
    private void SaveHeld(WindowEntry entry)
    {
        if (entry.Held is not { } held)
            return;

        entry.Held = null;
        if (held.Clean)
            SaveIfOnlyChange(entry, entry.Document, held.Rev);
    }

    /// <summary>
    /// The save after the window's own commit when the profile had no unsaved changes just before it applied, as a
    /// client's placement controls and an AI app's tools save theirs: a save that names the commit's own revision
    /// (<see cref="ProfileDocument.Save(HistoryRequest)"/>), so a change another client made after it is refused and
    /// nothing is saved. A profile with other unsaved changes isn't saved: the commit stays with them. A save that fails
    /// before the document is replaced leaves the commit unsaved, kept as any unsaved change is, and the window where it
    /// is; one that replaced it but couldn't flush its folder (<see cref="SavedNotFlushedException"/>) has saved it, not
    /// confirmed on disk. A profile deleted meanwhile (its document disposed, or no longer in the store) saves nothing.
    /// </summary>
    private void SaveIfOnlyChange(WindowEntry entry, ProfileDocument doc, long rev)
    {
        try
        {
            doc.Save(new HistoryRequest { Instance = doc.Instance, BaseRev = rev });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or KeyNotFoundException)
        {
            // Deleted meanwhile; the workspace's event closes the window.
        }
        catch (SavedNotFlushedException ex)
        {
            logger.LogWarning(ex, "The desktop window's change to profile {ProfileId} is saved, but the save couldn't confirm it is on disk", entry.ProfileId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The desktop window's change to profile {ProfileId} couldn't be saved; it stays unsaved", entry.ProfileId);
        }
    }

    private void ShowFrame(WindowEntry entry, DesktopFrame frame)
    {
        if (frame.Scale < 1 && _reduced.Add(entry.ProfileId))
        {
            logger.LogInformation("Profile {ProfileId} is over the frame pixel cap; its desktop window shows it at reduced resolution ({Scale}×), stretched to fill",
                entry.ProfileId, frame.Scale);
        }

        entry.Window.ShowFrame(frame);
    }

    private void OnSinkClosed(WindowEntry entry, DesktopWindowSink sink, FrameSinkClosedReason reason)
    {
        // A sink detached since (the window hidden) is no longer the window's.
        if (entry.Closed || entry.Sink != sink)
            return;

        // Deleted profiles and shutdown close the window through the workspace and StopAsync.
        if (reason == FrameSinkClosedReason.Unrenderable)
            logger.LogWarning("Profile {ProfileId} is too large to render; its desktop window stops updating", entry.ProfileId);
        entry.Sink = null;
    }

    private sealed class WindowEntry(string profileId, ProfileDocument document, IDesktopWindow window)
    {
        public string ProfileId { get; } = profileId;

        public ProfileDocument Document { get; } = document;

        public IDesktopWindow Window { get; } = window;

        public DesktopWindowSink? Sink { get; set; }

        /// <summary>The window is hidden (none of its displays connected) and its sink detached: showing it attaches a new one.</summary>
        public bool SinkHidden { get; set; }

        /// <summary>Where the window was last placed or its resize committed: a release there is no move.</summary>
        public PointRect? Settled { get; set; }

        /// <summary>
        /// The frame the system last moved the window to while the displays settled, reported as a resize and ignored
        /// (<see cref="OnUserResized"/>), until the window is placed again or the button is up: a release there is no move.
        /// </summary>
        public PointRect? Relocated { get; set; }

        /// <summary>The settings the window was last placed by.</summary>
        public Settings? Placed { get; set; }

        public bool ResizePending { get; set; }

        /// <summary>The window's frame and backing scale at the pending resize's last change, and the display then holding its top-left.</summary>
        public PointRect ResizeFrame { get; set; }

        public double ResizeScale { get; set; }

        public DisplayInfo? ResizeDisplay { get; set; }

        public ITimer? ResizeTimer { get; set; }

        /// <summary>Counts the resize's changes; a resize timer commits only for the latest.</summary>
        public int ResizeGeneration { get; set; }

        /// <summary>A display went away or changed during the current press: its release is no move.</summary>
        public bool DisplaysChanged { get; set; }

        /// <summary>
        /// The current press's last commit, made while the button was still down (<see cref="IDesktopWindow.UserMoving"/>),
        /// and whether the release may save it: the profile had no unsaved changes before the press's first commit, and
        /// each later one read the one before it. Saved, or dropped, at the release; null when no commit waits for one.
        /// </summary>
        public (long Rev, bool Clean)? Held { get; set; }

        public bool Closed { get; set; }
    }

    /// <summary>The profile's window settings, from its current generation (no lock).</summary>
    private sealed record Settings(bool Active, DesktopWindowStyle Style, int Width, int Height, WindowSettings? Window)
    {
        public static Settings Read(ProfileDocument doc)
        {
            var p = doc.Current.Document.Profile;
            var w = p.Window;
            return new Settings(w?.Enabled == true, new DesktopWindowStyle(LevelOf(w?.Level), w?.Draggable ?? true, w?.Resizable ?? true, w?.ClickThrough == true),
                Math.Max(1, p.Width), Math.Max(1, p.Height), w);
        }

        /// <summary><c>window.level</c>: <c>top</c>, <c>desktop</c>, else (absent, <c>normal</c>) normal.</summary>
        private static WindowLevel LevelOf(string? level) => level switch
        {
            "top" => WindowLevel.Top,
            "desktop" => WindowLevel.Desktop,
            _ => WindowLevel.Normal,
        };
    }
}
