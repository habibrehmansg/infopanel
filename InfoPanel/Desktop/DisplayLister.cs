namespace InfoPanel.Desktop;

/// <summary>
/// Lists the displays on the platform's UI thread for callers off it, with at most one listing posted at a time: a caller
/// that comes while one is posted and hasn't started waits for that one, whose result is read after it came. Each caller
/// waits only its own bound. A posted listing that no caller waits for any more when its turn comes (they all gave up or
/// went away) doesn't ask the platform, so callers during a stalled UI thread leave one cheap callback behind, not one
/// listing each.
/// </summary>
public sealed class DisplayLister(IDesktopPlatform platform)
{
    private readonly Lock _lock = new();
    private Listing? _posted;

    /// <summary>
    /// The displays (<see cref="IDesktopPlatform.GetDisplays"/>), read after this call; waits at most
    /// <paramref name="wait"/> on <paramref name="time"/> (then <see cref="TimeoutException"/>). The platform's
    /// exception, if it throws.
    /// </summary>
    public async Task<IReadOnlyList<DisplayInfo>> ListAsync(TimeSpan wait, TimeProvider time, CancellationToken cancellationToken)
    {
        Listing listing;
        var post = false;
        lock (_lock)
        {
            if (_posted == null)
            {
                _posted = new Listing();
                post = true;
            }

            listing = _posted;
            listing.Waiting++;
        }

        if (post)
        {
            try
            {
                platform.Post(() => Run(listing));
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    if (_posted == listing)
                        _posted = null;
                }

                listing.Result.TrySetException(ex);
            }
        }

        try
        {
            return await listing.Result.Task.WaitAsync(wait, time, cancellationToken);
        }
        finally
        {
            lock (_lock)
                listing.Waiting--;
        }
    }

    private void Run(Listing listing)
    {
        lock (_lock)
        {
            // Callers from now on post a listing of their own, read after they came.
            if (_posted == listing)
                _posted = null;
            if (listing.Waiting == 0)
            {
                listing.Result.TrySetCanceled();
                return;
            }
        }

        try
        {
            listing.Result.TrySetResult(platform.GetDisplays());
        }
        catch (Exception ex)
        {
            listing.Result.TrySetException(ex);
        }
    }

    private sealed class Listing
    {
        public TaskCompletionSource<IReadOnlyList<DisplayInfo>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Callers waiting for it (under the lock).</summary>
        public int Waiting { get; set; }
    }
}
