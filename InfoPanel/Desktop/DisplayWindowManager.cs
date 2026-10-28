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
/// <item>Applies the profile's <c>width</c> and <c>height</c> and its <c>window</c> (<c>enabled</c>, <c>topmost</c>,
/// <c>draggable</c>, <c>resizable</c>, <c>x</c>, <c>y</c>, <c>display</c>, <c>strict</c>) as they change, and re-matches
/// displays when they change (<see cref="DisplayMatcher"/>). Its own moves never produce commits.</item>
/// <item>The window's own changes are <c>profile</c> commits with a <c>gesture:</c> coalescing key of their own and the
/// revision they read as <c>baseRev</c> (undoable, dirty, saved by Save; read again and retried on a conflict): one per
/// finished drag (<c>window</c> with the <c>x</c>, <c>y</c> and
/// <c>display</c> of the display holding the window's top-left), one per resize after <see cref="ResizeQuiet"/>
/// without changes (<c>width</c>, <c>height</c>, and <c>window.x</c>/<c>y</c> relative to the window's display).</item>
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
    /// <summary>How long a window's size must stay unchanged before a resize becomes an edit (1.x: 300 ms).</summary>
    public static readonly TimeSpan ResizeQuiet = TimeSpan.FromMilliseconds(300);

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
                foreach (var id in _windows.Keys.ToList())
                    CloseWindow(id);
                foreach (var (_, doc) in _documents)
                    doc.Changed -= OnDocumentChanged;
                _documents.Clear();
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

    private void OnDisplaysChanged() => platform.Post(RematchDisplays);

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (e.Change is not (DocumentChange.Saved or DocumentChange.History) && sender is ProfileDocument doc)
            platform.Post(() => Reconcile(doc.Id));
    }

    /// <summary>UI thread: follows the profile list (opening new profiles, dropping deleted ones), then reconciles every window.</summary>
    private void SyncProfiles()
    {
        if (_stopped)
            return;

        _displays ??= platform.GetDisplays();
        var listed = workspace.List();
        var ids = listed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
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
    /// is holding a press on, out of its way: that press's release is then no move (<see cref="OnUserMoved"/>).
    /// </summary>
    private void RematchDisplays()
    {
        var displays = platform.GetDisplays();
        if (_displays == null || _displays.Except(displays).Any())
        {
            foreach (var entry in _windows.Values.Where(e => e.Window.UserMoving))
                entry.DisplaysChanged = true;
        }

        _displays = displays;
        ReconcileAll();
    }

    private void ReconcileAll()
    {
        foreach (var id in _documents.Keys.ToList())
            Reconcile(id);
    }

    /// <summary>UI thread: makes the profile's window match the profile (open or closed, style, place).</summary>
    private void Reconcile(string id)
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

        entry.Window.SetStyle(settings.Style);
        if (!entry.ResizePending)
            Place(entry, settings);
    }

    private WindowEntry OpenWindow(string id, ProfileDocument doc, Settings settings)
    {
        var displays = platform.GetDisplays();
        var scale = displays.FirstOrDefault()?.Scale ?? 1;
        var window = platform.CreateWindow(settings.Style, new PointRect(0, 0, settings.Width / scale, settings.Height / scale));
        var entry = new WindowEntry(id, doc, window);
        entry.Sink = new DesktopWindowSink(id, platform, FrameRate, frame => ShowFrame(entry, frame), reason => OnSinkClosed(entry, reason));
        window.UserMoved += () => OnUserMoved(entry);
        window.UserResized += () => OnUserResized(entry);
        window.BackingScaleChanged += () => OnBackingScaleChanged(entry);
        _windows[id] = entry;

        try
        {
            if (!render.Attach(entry.Sink, doc))
                entry.Sink = null;
        }
        catch (ObjectDisposedException ex)
        {
            logger.LogWarning(ex, "Profile {ProfileId} can't be rendered to its desktop window", id);
            entry.Sink = null;
        }

        logger.LogInformation("Showing profile {ProfileId} as a desktop window", id);
        return entry;
    }

    private void CloseWindow(string id)
    {
        if (!_windows.Remove(id, out var entry))
            return;

        entry.Closed = true;
        entry.ResizeTimer?.Dispose();
        if (entry.Sink is { } sink)
        {
            sink.Retire();
            render.Detach(sink);
        }

        entry.Window.Dispose();
        logger.LogInformation("Closed the desktop window of profile {ProfileId}", id);
    }

    /// <summary>
    /// Puts the window on its matched display, or hides it when there is none (strict matching). During a drag it
    /// stays where the user has it: placing it then (a scale or display change, any other profile's sync) would move
    /// it from under the mouse, back to where the profile has it. The release (<see cref="OnUserMoved"/>) places it.
    /// </summary>
    private void Place(WindowEntry entry, Settings settings)
    {
        var window = entry.Window;
        var displays = platform.GetDisplays();
        var display = DisplayMatcher.Match(displays, settings.Target, settings.Strict);
        entry.Display = display;
        if (display == null)
        {
            window.SetVisible(false);
            return;
        }

        if (window.UserMoving)
            return;

        // Sized at the scale of the display that will hold the window's top-left; if the window lands on a display
        // of another scale (it is mostly on that one), sized again at the window's own scale.
        var origin = DisplayMatcher.Frame(display, settings.WindowX, settings.WindowY, 1, 1, 1);
        var sizeScale = DisplayMatcher.At(displays, origin.X, origin.Y)?.Scale ?? display.Scale;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var frame = DisplayMatcher.Frame(display, settings.WindowX, settings.WindowY, settings.Width, settings.Height, sizeScale);
            if (!DisplayMatcher.SameFrame(window.Frame, frame))
                window.SetFrame(frame);
            if (window.BackingScale == sizeScale)
                break;
            sizeScale = window.BackingScale;
        }

        entry.Settled = window.Frame;
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
    /// the target, the position relative to it. A click, the end of a resize, or a press during which a display went
    /// away or changed, is no move.
    /// </summary>
    private void OnUserMoved(WindowEntry entry)
    {
        if (entry.Closed || _stopped)
            return;

        var frame = entry.Window.Frame;
        var displaysChanged = entry.DisplaysChanged;
        entry.DisplaysChanged = false;

        // A drag released before the resize ahead of it is quiet: the resize is its own edit first, at its last frame.
        if (entry.ResizePending && entry.ResizeFrame != frame)
            EditResize(entry);

        if (!displaysChanged && !entry.ResizePending && entry.Settled != frame && DisplayMatcher.At(platform.GetDisplays(), frame.X, frame.Y) is { } display)
        {
            var (x, y) = DisplayMatcher.Position(display, frame);
            Edit(entry, x, y, DisplayMatcher.TargetOf(display), null, display);
        }

        // Whether or not anything changed, the window goes to exactly the profile's place, sized for its scale there.
        Reconcile(entry.ProfileId);
    }

    private void OnUserResized(WindowEntry entry)
    {
        if (entry.Closed || _stopped)
            return;

        entry.ResizePending = true;
        entry.ResizeFrame = entry.Window.Frame;
        entry.ResizeScale = entry.Window.BackingScale;
        var generation = ++entry.ResizeGeneration;
        entry.ResizeTimer?.Dispose();
        entry.ResizeTimer = time.CreateTimer(_ => platform.Post(() => CommitResize(entry, generation)), null, ResizeQuiet, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The size quiet for <see cref="ResizeQuiet"/>: <c>width</c>/<c>height</c> from the window's size in backing
    /// pixels, and <c>window.x</c>/<c>y</c> relative to the window's display. A timer that fired before a later
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

    /// <summary>Ends the pending resize with its commit, from the window's frame and scale at the resize's last change.</summary>
    private void EditResize(WindowEntry entry)
    {
        entry.ResizeTimer?.Dispose();
        entry.ResizeTimer = null;
        entry.ResizePending = false;
        var frame = entry.ResizeFrame;
        var scale = entry.ResizeScale;
        var width = Math.Clamp((int)Math.Round(frame.Width * scale), 1, Workspace.MaxDimension);
        var height = Math.Clamp((int)Math.Round(frame.Height * scale), 1, Workspace.MaxDimension);
        var display = entry.Display ?? DisplayMatcher.At(platform.GetDisplays(), frame.X, frame.Y);
        if (display != null)
        {
            var (x, y) = DisplayMatcher.Position(display, frame);
            Edit(entry, x, y, null, (width, height), display);
        }

        entry.Settled = frame;
    }

    /// <summary>
    /// Commits the window's place (<paramref name="x"/>, <paramref name="y"/> relative to <paramref name="display"/>), its
    /// display when it was dragged to one, and its size when it was resized, as one <c>profile</c> op with a
    /// <c>gesture:</c> key of its own. The op sets the whole <c>window</c> as read, so it carries that read's revision
    /// as its <c>baseRev</c>: when another change to the profile came first (409), the latest window is read and the
    /// place applied to it again, up to <see cref="CommitAttempts"/> times, so another client's <c>topmost</c>,
    /// <c>resizable</c> or <c>enabled</c> is never undone. A commit that changes nothing makes no revision.
    /// </summary>
    private void Edit(WindowEntry entry, int x, int y, DisplayMatch? target, (int Width, int Height)? size, DisplayInfo display)
    {
        var doc = entry.Document;
        var key = $"gesture:{_gestureClient}:{++_gestures}";
        Generation current;
        CommitOutcome outcome;
        for (var attempt = 1; ; attempt++)
        {
            current = doc.Current;
            var profile = current.Document.Profile;
            var window = (profile.Window ?? new WindowSettings { Enabled = true }) with { X = x, Y = y };
            if (target != null)
                window = window with { Display = target };

            BeforeCommit?.Invoke(entry.ProfileId);
            try
            {
                outcome = doc.Commit(new CommitRequest
                {
                    OpId = Guid.NewGuid().ToString("N"),
                    Instance = doc.Instance,
                    BaseRev = current.Rev,
                    CoalesceKey = key,
                    Ops = [new ProfileOp { BaseRev = current.Rev, Set = new ProfileFields { Window = window, Width = size?.Width, Height = size?.Height } }],
                });
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

    private void OnSinkClosed(WindowEntry entry, FrameSinkClosedReason reason)
    {
        if (entry.Closed)
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

        public DisplayInfo? Display { get; set; }

        /// <summary>Where the window was last placed or its resize committed: a release there is no move.</summary>
        public PointRect? Settled { get; set; }

        public bool ResizePending { get; set; }

        /// <summary>The window's frame and backing scale at the pending resize's last change.</summary>
        public PointRect ResizeFrame { get; set; }

        public double ResizeScale { get; set; }

        public ITimer? ResizeTimer { get; set; }

        /// <summary>Counts the resize's changes; a resize timer commits only for the latest.</summary>
        public int ResizeGeneration { get; set; }

        /// <summary>A display went away or changed during the current press: its release is no move.</summary>
        public bool DisplaysChanged { get; set; }

        public bool Closed { get; set; }
    }

    /// <summary>The profile's window settings, from its current generation (no lock).</summary>
    private sealed record Settings(bool Active, DesktopWindowStyle Style, int Width, int Height, int WindowX, int WindowY,
        DisplayMatch? Target, bool Strict)
    {
        public static Settings Read(ProfileDocument doc)
        {
            var p = doc.Current.Document.Profile;
            var w = p.Window;
            return new Settings(w?.Enabled == true, new DesktopWindowStyle(w?.Topmost ?? false, w?.Draggable ?? true, w?.Resizable ?? true),
                Math.Max(1, p.Width), Math.Max(1, p.Height), w?.X ?? 0, w?.Y ?? 0, w?.Display, w?.Strict ?? false);
        }
    }
}
