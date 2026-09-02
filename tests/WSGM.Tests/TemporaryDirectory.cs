namespace WSGM.Device.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "wsgm-device-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string GetPath(params string[] segments)
    {
        string path = Root;
        foreach (string segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (
                attempt < 4
                && exception is IOException or UnauthorizedAccessException)
            {
                // Collectible plugin load contexts release their mapped package files only after
                // collection. Test cleanup waits for that documented unload boundary.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(20);
            }
        }
    }
}
