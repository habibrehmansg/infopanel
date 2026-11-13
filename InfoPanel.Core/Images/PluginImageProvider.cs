using System;

namespace InfoPanel.Images;

/// <summary>
/// The <c>plugin</c> media provider (spec/docs/providers.md): a plugin's own picture, as the plugins source keeps it for
/// its image sensor. A source's <c>ref</c> is the image sensor's reading's text, the reference naming one of the pictures
/// the plugins source keeps (each image entry's current and previous); any other reference (empty text, another
/// provider's, a picture no longer kept), and every reference in an engine without plugins, is answered no picture with
/// <c>media.provider</c>, "No picture.": the item draws nothing in its box. It fetches nothing and reaches no address: a
/// picture's bytes are those its plugin's host sent, inspected when the source took them, kept and decoded as
/// <see cref="KeptPictureProvider"/> says, and let go of within a second of the source letting go of them (each sweep
/// looks). What a picture's failure says is its code and "Couldn't load the picture.".
/// </summary>
public sealed class PluginImageProvider : KeptPictureProvider
{
    public const string Name = "plugin";

    /// <summary>What every key of its own in the store and the cache starts with; no other provider's key does.</summary>
    internal const string KeyPrefix = FetchedMediaStore.Prefix + "plugin:";

    /// <summary>The text of every picture's failure, whatever its code.</summary>
    internal const string Failed = "Couldn't load the picture.";

    /// <param name="pictures">The plugins source's pictures; null in an engine without it.</param>
    /// <param name="cache">The engine's media cache, which decodes the pictures from <paramref name="store"/>.</param>
    /// <param name="store">The fetched media's store, where a picture's bytes are kept while it is asked for.</param>
    /// <param name="time">The clock that times the references no tick asks for.</param>
    public PluginImageProvider(IKeptPictures? pictures, MediaSampleCache cache, FetchedMediaStore store, TimeProvider? time = null)
        : base(pictures, cache, store, KeyPrefix, new MediaStatus("media.provider", "No picture."), Failed,
            new MediaStatus("media.loading", "Loading the picture."), sweepUnkept: true, web: null, time)
    {
    }
}
