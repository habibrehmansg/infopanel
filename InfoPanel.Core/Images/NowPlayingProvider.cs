using System;
using InfoPanel.Sensors.NowPlaying;

namespace InfoPanel.Images;

/// <summary>
/// The <c>now-playing</c> media provider (spec/docs/providers.md): the cover art of what this computer plays, as the
/// now-playing source read it (<see cref="NowPlayingArt"/>). A source's <c>ref</c> is the <c>art</c> reading's text, a
/// token naming one of the covers the source keeps; any other reference, and every reference in an engine without the
/// source, is answered no cover with <c>media.provider</c>, "No cover art.": a sample that draws nothing, so an item with
/// nothing playing is transparent rather than the missing-media placeholder, which a cover that fails still draws. It
/// fetches nothing and connects nowhere: a cover's bytes are the reader's, kept and decoded as
/// <see cref="KeptPictureProvider"/> says, and a cover the source lets go of (pushed out by a newer one, or every cover
/// when it stops reading) leaves the store and the cache at once. A cover a player gives as a web address is asked of the
/// web image provider (<c>http</c>) for that address, under its rules, as a private address and with no refresh. What a
/// cover's failure says is its code and "Couldn't load the cover art.": a token names the cover of what plays.
/// </summary>
public sealed class NowPlayingProvider : KeptPictureProvider
{
    public const string Name = "now-playing";

    /// <summary>What every key of its own in the store and the cache starts with; no other provider's key does.</summary>
    internal const string KeyPrefix = FetchedMediaStore.Prefix + "now-playing:";

    /// <summary>The text of every cover's failure, whatever its code.</summary>
    internal const string Failed = "Couldn't load the cover art.";

    private readonly NowPlayingArt? _art;

    /// <param name="art">The now-playing source's covers; null in an engine without the source (mock mode, a replay).</param>
    /// <param name="cache">The engine's media cache, which decodes the covers from <paramref name="store"/>.</param>
    /// <param name="store">The fetched media's store, where a cover's bytes are kept while it is asked for.</param>
    /// <param name="web">The provider a cover given as a web address is asked of (<c>http</c>); none leaves such covers out.</param>
    /// <param name="time">The clock that times the tokens no tick asks for.</param>
    public NowPlayingProvider(NowPlayingArt? art, MediaSampleCache cache, FetchedMediaStore store, IMediaProvider? web = null, TimeProvider? time = null)
        : base(art, cache, store, KeyPrefix, new MediaStatus("media.provider", "No cover art."), Failed,
            new MediaStatus("media.loading", "Loading the cover art."), sweepUnkept: false, web, time)
    {
        _art = art;
        // A cover the source lets go of leaves the store and the cache with it.
        if (_art is not null) _art.Dropped += Drop;
    }

    /// <summary>The store's and the cache's key of the cover whose bytes have the SHA-256 <paramref name="sha256"/>.</summary>
    internal static string KeyOf(string sha256) => KeyPrefix + "sha256:" + sha256;

    protected override void OnDisposed()
    {
        if (_art is not null) _art.Dropped -= Drop;
    }
}
