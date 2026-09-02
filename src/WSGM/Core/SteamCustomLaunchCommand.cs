using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Steam-native fields for a custom launch action.</summary>
/// <param name="LaunchOptions">Launch Options for a regular Steam title.</param>
/// <param name="ShortcutTarget">Target for a non-Steam shortcut.</param>
/// <param name="ShortcutArguments">Launch Arguments for a non-Steam shortcut.</param>
internal readonly record struct SteamCustomLaunchFields(
    string LaunchOptions, string ShortcutTarget, string ShortcutArguments);

/// <summary>Builds Steam-native custom launch commands without a WSGM wrapper.</summary>
internal static class SteamCustomLaunchCommand
{
    internal static SteamCustomLaunchFields Build(
        string path, string? customArguments, string? commandProcessor = null,
        string? powerShell = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var selected = Quote(path);
        var arguments = NormalizeArguments(customArguments);
        var suffix = arguments.Length == 0 ? "" : " " + arguments;

        return extension switch
        {
            ".exe" => new($"{selected}{suffix} %command%", selected, arguments),
            ".cmd" or ".bat" => BuildScript(
                Quote(commandProcessor ?? ResolveCommandProcessor()),
                $"/d /s /c call {selected}{suffix}"),
            ".ps1" => BuildScript(
                Quote(powerShell ?? ResolvePowerShell()),
                $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {selected}{suffix}"),
            _ => throw new ArgumentException("Select an EXE, CMD, BAT, or PS1 file.", nameof(path)),
        };
    }

    internal static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static SteamCustomLaunchFields BuildScript(string host, string arguments) =>
        new($"{host} {arguments} %command%", host, arguments);

    private static string NormalizeArguments(string? arguments)
    {
        var value = arguments?.Trim() ?? "";
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Custom arguments must be a single line.", nameof(arguments));
        }
        return value;
    }

    private static string ResolveCommandProcessor() =>
        Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } path
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static string ResolvePowerShell() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Always-wrapping quote for Steam-facing command strings (Launch Options and
    /// shortcut Target fields), shared with the launch-wrapper command builder. Distinct from
    /// <see cref="SelfElevation.Quote"/>, which quotes conditionally for argv round-trips.</summary>
    internal static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
