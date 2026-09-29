using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Tests;

internal sealed class GlyphTestPackageSource(
    string profileId,
    IReadOnlyDictionary<string, byte[]> files) : IGlyphPackageSource
{
    public IReadOnlyList<string> EnumerateProfileIds() => [profileId];

    public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
    {
        if (files.TryGetValue(relativePath, out byte[]? asset)
            && asset.Length <= maximumBytes)
        {
            bytes = [.. asset];
            return true;
        }

        bytes = [];
        return false;
    }
}
