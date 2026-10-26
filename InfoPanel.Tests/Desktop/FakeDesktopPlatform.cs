using System.Collections.Concurrent;
using InfoPanel.Desktop;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// A desktop platform with its own UI thread (like the macOS main thread) and windows that only record what they're
/// told. A window's backing scale is the scale of the display holding its top-left, as when it sits on that display.
/// Tests read and drive it on the UI thread through <see cref="InvokeAsync{T}"/>.
/// </summary>
public sealed class FakeDesktopPlatform : IDesktopPlatform, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private int _uiThreadId;

    public FakeDesktopPlatform(params DisplayInfo[] displays)
    {
        Displays = [.. displays];
        _thread = new Thread(Run) { IsBackground = true, Name = "Fake UI thread" };
        _thread.Start();
    }

    public List<DisplayInfo> Displays { get; set; }

    /// <summary>Every window created, closed ones included (UI thread).</summary>
    public List<FakeWindow> Created { get; } = [];

    /// <summary>Exceptions thrown by posted actions.</summary>
    public ConcurrentQueue<Exception> Errors { get; } = [];

    public bool IsSupported => true;

    public string? UnsupportedMessage => null;

    public event Action? DisplaysChanged;

    public void Post(Action action) => _queue.Add(action);

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        AssertUiThread();
        return [.. Displays];
    }

    public IDesktopWindow CreateWindow(DesktopWindowStyle style, PointRect frame)
    {
        AssertUiThread();
        var window = new FakeWindow(this, style, frame);
        Created.Add(window);
        return window;
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread after everything posted before it.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                result.SetResult(func());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        return result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>Waits until everything posted so far has run.</summary>
    public Task IdleAsync() => InvokeAsync(() => { });

    /// <summary>Changes the displays, raises <see cref="DisplaysChanged"/> on the UI thread and waits for what it posted.</summary>
    public async Task ChangeDisplaysAsync(params DisplayInfo[] displays)
    {
        await InvokeAsync(() =>
        {
            Displays = [.. displays];
            DisplaysChanged?.Invoke();
        });
        await IdleAsync();
    }

    internal double ScaleAt(PointRect frame) =>
        Displays.FirstOrDefault(d => d.Frame.Contains(frame.X, frame.Y))?.Scale ?? Displays.FirstOrDefault()?.Scale ?? 1;

    internal void AssertUiThread()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
            throw new InvalidOperationException("Called off the UI thread.");
    }

    private void Run()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Errors.Enqueue(ex);
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}

public sealed class FakeWindow(FakeDesktopPlatform platform, DesktopWindowStyle style, PointRect frame) : IDesktopWindow
{
    public PointRect Frame { get; private set; } = frame;

    public double BackingScale => platform.ScaleAt(Frame);

    public bool Visible { get; private set; }

    public DesktopWindowStyle Style { get; private set; } = style;

    public bool Disposed { get; private set; }

    /// <summary>Calls to <see cref="SetFrame"/>.</summary>
    public int FrameChanges { get; private set; }

    /// <summary>The last frame shown (a copy of its pixels, trimmed to the frame).</summary>
    public DesktopFrame? LastFrame { get; private set; }

    public int FramesShown { get; private set; }

    public event Action? UserMoved;

    public event Action? UserResized;

    public event Action? BackingScaleChanged;

    public void SetFrame(PointRect frame)
    {
        platform.AssertUiThread();
        var scale = BackingScale;
        Frame = frame;
        FrameChanges++;
        if (BackingScale != scale)
            BackingScaleChanged?.Invoke();
    }

    public void SetStyle(DesktopWindowStyle style)
    {
        platform.AssertUiThread();
        Style = style;
    }

    public void SetVisible(bool visible)
    {
        platform.AssertUiThread();
        Visible = visible;
    }

    public void ShowFrame(DesktopFrame frame)
    {
        platform.AssertUiThread();
        LastFrame = frame with { Pixels = frame.Pixels.AsSpan(0, frame.RowBytes * frame.Height).ToArray() };
        FramesShown++;
    }

    public void Dispose()
    {
        platform.AssertUiThread();
        Disposed = true;
        Visible = false;
    }

    /// <summary>The user dragged the window to <paramref name="frame"/> and let go (UI thread).</summary>
    public void UserMove(PointRect frame)
    {
        platform.AssertUiThread();
        Frame = frame;
        UserMoved?.Invoke();
    }

    /// <summary>The user resized the window to <paramref name="frame"/> (UI thread).</summary>
    public void UserResize(PointRect frame)
    {
        platform.AssertUiThread();
        Frame = frame;
        UserResized?.Invoke();
    }
}
