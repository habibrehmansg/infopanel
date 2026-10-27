using InfoPanel.Editing;
using InfoPanel.Hosting;
using InfoPanel.Models;
using InfoPanel.Output;

namespace InfoPanel.Desktop;

/// <summary>
/// Shows every profile with <c>Active = true</c> as a borderless desktop window (1.x's <c>DisplayWindow</c>), drawn
/// by the profile's render loop at scale 1 through a <see cref="DesktopWindowSink"/>.
/// <list type="bullet">
/// <item>Follows the <see cref="Workspace"/>: profiles added, deleted and listed (<see cref="Workspace.ProfilesChanged"/>)
/// and every document's <see cref="ProfileDocument.Changed"/> (edits, undo, redo, revert). Every profile is opened
/// so its changes are seen.</item>
/// <item>Applies <c>Active</c>, <c>Topmost</c>, <c>Drag</c>, <c>Resize</c>, <c>Width</c>, <c>Height</c>,
/// <c>WindowX</c>, <c>WindowY</c>, <c>TargetWindow</c> and <c>StrictWindowMatching</c> as they change, and re-matches
/// displays when they change (<see cref="DisplayMatcher"/>). Its own moves never produce edits.</item>
/// <item>The window's own changes are profile edits (<see cref="ProfileDocument.PlaceWindow"/>: undoable, dirty,
/// saved by Save): one per finished drag (<c>WindowX</c>, <c>WindowY</c>, <c>TargetWindow</c> of the display holding
/// the window's top-left), one per resize after <see cref="ResizeQuiet"/> without changes (<c>Width</c>,
/// <c>Height</c>, and <c>WindowX</c>/<c>WindowY</c> when the top-left moved).</item>
/// <item>All its state lives on the platform's UI thread: every event is posted there (<see cref="IDesktopPlatform.Post"/>).</item>
/// </list>
/// Nothing happens with <c>--no-windows</c>; on a platform that can't show windows it logs
/// <see cref="IDesktopPlatform.UnsupportedMessage"/> once if a profile is active, and does nothing else.
/// </summary>
public sealed class DisplayWindowManager(
    IDesktopPlatform platform,
    Workspace workspace,
    RenderService render,
    EngineOptions options,
    TimeProvider time,
    ILogger<DisplayWindowManager> logger) : IHostedService
{
    /// <summary>How long a window's size must stay unchanged before a resize becomes an edit (1.x: 300 ms).</summary>
    public static readonly TimeSpan ResizeQuiet = TimeSpan.FromMilliseconds(300);

    private readonly Dictionary<Guid, ProfileDocument> _documents = [];
    private readonly Dictionary<Guid, WindowEntry> _windows = [];
    private readonly HashSet<Guid> _unreadable = [];
    private readonly HashSet<Guid> _reduced = [];
    private IReadOnlyList<DisplayInfo>? _displays;
    private bool _running;
    private bool _stopped;

    /// <summary>True when the manager shows windows (not <c>--no-windows</c>, and the platform can).</summary>
    public bool Enabled => !options.NoWindows && platform.IsSupported;

    /// <summary>The profiles that have a window (UI thread; for tests).</summary>
    public IReadOnlyCollection<Guid> WindowProfiles => _windows.Keys;

    /// <summary>The profile's window, or null (UI thread; for tests).</summary>
    public IDesktopWindow? WindowOf(Guid profileId) => _windows.GetValueOrDefault(profileId)?.Window;

    /// <summary>The profile's frame sink, or null (UI thread; for tests).</summary>
    public DesktopWindowSink? SinkOf(Guid profileId) => _windows.GetValueOrDefault(profileId)?.Sink;

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
        workspace.ProfilesChanged += OnProfilesChanged;
        platform.DisplaysChanged += OnDisplaysChanged;
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

    /// <summary>Profiles are read from <c>profiles.xml</c> as saved: no document is opened just to say this.</summary>
    private void LogUnsupported()
    {
        if (platform.UnsupportedMessage is not { } message)
            return;

        try
        {
            if (workspace.Store.LoadProfiles().Any(p => p.Active))
                logger.LogInformation("{Message}", message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The workspace reports an unreadable list on its own.
        }
    }

    private void OnProfilesChanged(object? sender, ProfilesChangedEventArgs e) => platform.Post(SyncProfiles);

    private void OnDisplaysChanged() => platform.Post(RematchDisplays);

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (e.Change != DocumentChange.Saved && sender is ProfileDocument doc)
            platform.Post(() => Reconcile(doc.Id));
    }

    /// <summary>UI thread: follows the profile list (opening new profiles, dropping deleted ones), then reconciles every window.</summary>
    private void SyncProfiles()
    {
        if (_stopped)
            return;

        _displays ??= platform.GetDisplays();
        var listed = workspace.ListProfiles().Select(p => p.Id).ToHashSet();
        foreach (var (id, doc) in _documents.ToList())
        {
            if (listed.Contains(id) && workspace.IsOpen(doc))
                continue;

            doc.Changed -= OnDocumentChanged;
            _documents.Remove(id);
            CloseWindow(id);
        }

        foreach (var id in listed)
        {
            if (_documents.ContainsKey(id))
                continue;

            ProfileDocument? doc;
            try
            {
                doc = workspace.Open(id);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Tried again on the next sync.
                if (_unreadable.Add(id))
                    logger.LogWarning(ex, "Profile {ProfileGuid} can't be read; it gets no desktop window", id);
                continue;
            }

            if (doc == null)
                continue;

            doc.Changed += OnDocumentChanged;
            _documents[id] = doc;
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
    private void Reconcile(Guid id)
    {
        if (_stopped || !_documents.TryGetValue(id, out var doc))
            return;

        var settings = WindowSettings.Read(doc);
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

    private WindowEntry OpenWindow(Guid id, ProfileDocument doc, WindowSettings settings)
    {
        var displays = platform.GetDisplays();
        var scale = displays.FirstOrDefault()?.Scale ?? 1;
        var window = platform.CreateWindow(settings.Style, new PointRect(0, 0, settings.Width / scale, settings.Height / scale));
        var entry = new WindowEntry(id, doc, window);
        entry.Sink = new DesktopWindowSink(id, platform, frame => ShowFrame(entry, frame), reason => OnSinkClosed(entry, reason));
        window.UserMoved += () => OnUserMoved(entry);
        window.UserResized += () => OnUserResized(entry);
        window.BackingScaleChanged += () => OnBackingScaleChanged(entry);
        _windows[id] = entry;

        try
        {
            if (!render.Attach(entry.Sink))
                entry.Sink = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ObjectDisposedException)
        {
            logger.LogWarning(ex, "Profile {ProfileGuid} can't be rendered to its desktop window", id);
            entry.Sink = null;
        }

        logger.LogInformation("Showing profile {ProfileGuid} as a desktop window", id);
        return entry;
    }

    private void CloseWindow(Guid id)
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
        logger.LogInformation("Closed the desktop window of profile {ProfileGuid}", id);
    }

    /// <summary>
    /// Puts the window on its matched display, or hides it when there is none (strict matching). During a drag it
    /// stays where the user has it: placing it then (a scale or display change, any other profile's sync) would move
    /// it from under the mouse, back to where the profile has it. The release (<see cref="OnUserMoved"/>) places it.
    /// </summary>
    private void Place(WindowEntry entry, WindowSettings settings)
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
            Edit(entry, new WindowPlacement(x, y, DisplayMatcher.TargetOf(display)), display);
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
    /// The size quiet for <see cref="ResizeQuiet"/>: <c>Width</c>/<c>Height</c> from the window's size in backing
    /// pixels, and <c>WindowX</c>/<c>WindowY</c> relative to the window's display. A timer that fired before a later
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

    /// <summary>Ends the pending resize with its edit, from the window's frame and scale at the resize's last change.</summary>
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
            Edit(entry, new WindowPlacement(x, y, Width: width, Height: height), display);
        }

        entry.Settled = frame;
    }

    /// <summary>Applies <paramref name="placement"/>, relative to <paramref name="display"/>, as a profile edit.</summary>
    private void Edit(WindowEntry entry, WindowPlacement placement, DisplayInfo display)
    {
        try
        {
            var before = entry.Document.Revision;
            if (entry.Document.PlaceWindow(placement).Revision == before)
                return;

            if (placement is { Width: { } width, Height: { } height })
            {
                logger.LogInformation("The desktop window of profile {ProfileGuid} was resized on the desktop to {Width}×{Height} at ({WindowX}, {WindowY}) on {Display}",
                    entry.ProfileId, width, height, placement.WindowX, placement.WindowY, display.Name);
            }
            else
            {
                logger.LogInformation("The desktop window of profile {ProfileGuid} was moved on the desktop to ({WindowX}, {WindowY}) on {Display}",
                    entry.ProfileId, placement.WindowX, placement.WindowY, display.Name);
            }
        }
        catch (EditException ex)
        {
            logger.LogWarning(ex, "The desktop window of profile {ProfileGuid} couldn't update the profile", entry.ProfileId);
        }
    }

    private void ShowFrame(WindowEntry entry, DesktopFrame frame)
    {
        if (frame.Scale < 1 && _reduced.Add(entry.ProfileId))
        {
            logger.LogInformation("Profile {ProfileGuid} is over the frame pixel cap; its desktop window shows it at reduced resolution ({Scale}×), stretched to fill",
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
            logger.LogWarning("Profile {ProfileGuid} is too large to render; its desktop window stops updating", entry.ProfileId);
        entry.Sink = null;
    }

    private sealed class WindowEntry(Guid profileId, ProfileDocument document, IDesktopWindow window)
    {
        public Guid ProfileId { get; } = profileId;

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

    /// <summary>The profile's window settings, read under the document's lock.</summary>
    private sealed record WindowSettings(bool Active, DesktopWindowStyle Style, int Width, int Height, int WindowX, int WindowY,
        TargetWindow? Target, bool Strict)
    {
        public static WindowSettings Read(ProfileDocument doc)
        {
            lock (doc.SyncRoot)
            {
                var p = doc.Profile;
                var target = p.TargetWindow is { } t ? new TargetWindow(t.X, t.Y, t.Width, t.Height, t.DeviceName) : null;
                return new WindowSettings(p.Active, new DesktopWindowStyle(p.Topmost, p.Drag, p.Resize), Math.Max(1, p.Width),
                    Math.Max(1, p.Height), p.WindowX, p.WindowY, target, p.StrictWindowMatching);
            }
        }
    }
}
