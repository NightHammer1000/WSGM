using Microsoft.Win32;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class RegistryValueSnapshotTests
{
    [Fact]
    [Trait("Category", "Registry")]
    public void AbsentValueIsCapturedAndRestoredByDeletingTheCurrentValue()
    {
        using var scope = new TestRegistryKey();
        var snapshot = CreateSnapshot();
        var config = new AppConfig();

        var captured = snapshot.ReadCurrent(scope.Key);
        snapshot.Capture(config, captured);
        scope.Key.SetValue("Shell", "WSGM.exe", RegistryValueKind.String);

        snapshot.Restore(scope.Key, config);

        Assert.False(captured.Exists);
        Assert.True(snapshot.IsCaptured(config));
        Assert.Null(scope.Key.GetValue("Shell", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [Fact]
    [Trait("Category", "Registry")]
    public void ExpandStringValueRoundTripsWithItsOriginalRegistryKind()
    {
        using var scope = new TestRegistryKey();
        var snapshot = CreateSnapshot();
        scope.Key.SetValue("Shell", "%TEMP%\\shell.exe", RegistryValueKind.ExpandString);
        var config = new AppConfig();
        snapshot.Capture(config, snapshot.ReadCurrent(scope.Key));
        scope.Key.SetValue("Shell", "changed", RegistryValueKind.String);

        snapshot.Restore(scope.Key, config);

        Assert.Equal("%TEMP%\\shell.exe", scope.Key.GetValue("Shell", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(RegistryValueKind.ExpandString, scope.Key.GetValueKind("Shell"));
    }

    private static RegistryValueSnapshot<string?> CreateSnapshot()
        => new(
            "Shell",
            absentValue: null,
            writeFallback: string.Empty,
            defaultKind: RegistryValueKind.String,
            coerce: static value => value as string ?? string.Empty,
            normalizeKind: static kind => kind == RegistryValueKind.ExpandString
                ? RegistryValueKind.ExpandString
                : RegistryValueKind.String,
            load: static config => new RegistryValueSnapshot<string?>.State(
                config.PreviousShellSnapshotCaptured,
                config.PreviousShellValueExists,
                config.PreviousShellValue,
                config.PreviousShellValueKind),
            store: static (config, state) =>
            {
                config.PreviousShellSnapshotCaptured = state.Captured;
                config.PreviousShellValueExists = state.Exists;
                config.PreviousShellValue = state.Value;
                config.PreviousShellValueKind = state.Kind;
            });

    private sealed class TestRegistryKey : IDisposable
    {
        public RegistryKey Key { get; } = Registry.CurrentUser.CreateSubKey(
            $"Software\\WSGM.Tests\\{Guid.NewGuid():N}")!;

        public void Dispose()
        {
            var path = Key.Name["HKEY_CURRENT_USER\\".Length..];
            Key.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            try
            {
                // CreateSubKey made the parent implicitly; leaving it behind is machine
                // residue from a suite that must leave none. DeleteSubKey throws while a
                // concurrent scope still has a child there — that is the correct no-op.
                Registry.CurrentUser.DeleteSubKey("Software\\WSGM.Tests", throwOnMissingSubKey: false);
            }
            catch
            {
                // Another live scope still owns a child key; it removes the parent instead.
            }
        }
    }
}
