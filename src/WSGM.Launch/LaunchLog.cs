using System;
using System.IO;
using System.Text;
using System.Threading;

namespace WSGM.Launch;

internal static class LaunchLog
{
    private const long MaximumBytes = 2 * 1024 * 1024;
    private const int RetainedArchives = 3;
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WSGM",
        "launch.log");

    internal static void Info(string message) => Write("info ", message);

    internal static void Warn(string message) => Write("warn ", message);

    internal static void Error(string message) => Write("error", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [pid {Environment.ProcessId}] {message}{Environment.NewLine}";
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        RotateIfNeeded();
                        File.AppendAllText(Path, line, Encoding.UTF8);
                        return;
                    }
                    catch (IOException) when (attempt < 3)
                    {
                        Thread.Sleep(15);
                    }
                }
            }
        }
        catch
        {
            // A launch wrapper must never fail merely because diagnostics cannot
            // be written (concurrent helper, full disk, damaged profile, ...).
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(Path) || new FileInfo(Path).Length < MaximumBytes)
        {
            return;
        }

        for (int index = RetainedArchives; index >= 1; index--)
        {
            string source = index == 1 ? Path : $"{Path}.{index - 1}";
            if (!File.Exists(source))
            {
                continue;
            }

            File.Move(source, $"{Path}.{index}", overwrite: true);
        }
    }
}
