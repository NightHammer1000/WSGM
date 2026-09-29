using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Tests;

/// <summary>
/// The diagnostic channel plugins write through, and the guarantees the layers above it rely on.
/// </summary>
/// <remarks>
/// These matter more than their size suggests. The channel exists because five separate device
/// faults were diagnosed by adding temporary instrumentation and rebuilding — the shipped plugin
/// could not say why it had done nothing. Instrumentation that throws, or that a plugin can use to
/// flood the log, would put the log back to being unreadable in the other direction.
/// </remarks>
public sealed class PluginTraceTests
{
    [Fact]
    public void TracingWithNoSinkInstalledIsSilentRatherThanFatal()
    {
        // A plugin traces from catch blocks and from startup paths that run before anything is
        // wired. If that could throw, instrumentation would itself be a source of faults and the
        // rational response would be to remove it.
        PluginTrace.Install(null);

        PluginTrace.Info("scope", "message");
        PluginTrace.Warn("scope", "message");
        PluginTrace.Error("scope", "message");
        PluginTrace.Failure("scope", "context", new InvalidOperationException("boom"));
    }

    [Fact]
    public void FailureRecordsTheExceptionTypeAndMessageNotJustTheType()
    {
        // The DllNotFoundException that stalled a device cycle reached the log as its type name
        // alone. The message was the entire diagnosis: it named the library.
        TestPluginHostAdapter host = new(1);
        PluginTrace.Install(host);
        try
        {
            PluginTrace.Failure(
                "loader",
                "starting the plugin",
                new DllNotFoundException("ole32.dll"));
        }
        finally
        {
            PluginTrace.Install(null);
        }

        (DeviceTraceLevel Level, string Scope, string Message) trace = Assert.Single(host.Traces);
        Assert.Equal(DeviceTraceLevel.Warn, trace.Level);
        Assert.Equal("loader", trace.Scope);
        Assert.Contains("starting the plugin", trace.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DllNotFoundException), trace.Message, StringComparison.Ordinal);
        Assert.Contains("ole32.dll", trace.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestAdapterRecordsTracesSoPluginDiagnosticsAreAssertable()
    {
        TestPluginHostAdapter host = new(3);
        host.Trace(DeviceTraceLevel.Info, "controller", "switching to DirectInput");
        host.Trace(DeviceTraceLevel.Warn, "wmi", "provider probe failed");

        IReadOnlyList<(DeviceTraceLevel Level, string Scope, string Message)> traces = host.Traces;
        Assert.Equal(2, traces.Count);
        Assert.Equal("controller", traces[0].Scope);
        Assert.Equal(DeviceTraceLevel.Warn, traces[1].Level);
    }

    [Fact]
    public void AnEmptyTraceIsDroppedRatherThanWrittenAsABlankLine()
    {
        TestPluginHostAdapter host = new(1);
        host.Trace(DeviceTraceLevel.Info, "scope", string.Empty);

        Assert.Empty(host.Traces);
    }
}
