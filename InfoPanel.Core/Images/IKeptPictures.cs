using System.Diagnostics.CodeAnalysis;

namespace InfoPanel.Images;

/// <summary>
/// The pictures a sensor source keeps in memory, each under its image sensor's reference, for the media provider that
/// draws them (<see cref="KeptPictureProvider"/>): Now Playing's covers, a plugin's pictures. Thread-safe.
/// </summary>
public interface IKeptPictures
{
    /// <summary>
    /// Whether a picture is kept under <paramref name="reference"/> now, and its <paramref name="address"/> when it was
    /// given as one (a web address, with no bytes): nothing that holds its bytes, so a caller that pauses after asking
    /// keeps none alive.
    /// </summary>
    bool Keeps(string reference, out string? address);

    /// <summary>
    /// The bytes of the picture kept under <paramref name="reference"/>, the source's own (never changed after), and a name
    /// for them the same for the same bytes (<paramref name="id"/>: hexadecimal of their SHA-256, whole or in part); false
    /// when none is kept, or the picture is an address.
    /// </summary>
    bool TryGet(string reference, [NotNullWhen(true)] out byte[]? bytes, [NotNullWhen(true)] out string? id);
}
