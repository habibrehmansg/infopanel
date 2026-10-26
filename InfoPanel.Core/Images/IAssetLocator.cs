using System;

namespace InfoPanel.Images
{
    /// <summary>
    /// Locates profile assets (images referenced by relative paths).
    /// </summary>
    public interface IAssetLocator
    {
        string GetProfileAssetsDirectory(Guid profileId);
    }
}
