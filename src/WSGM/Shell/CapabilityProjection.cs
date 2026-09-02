using System.Text.Json.Serialization;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Where a capability's UI is in the request cycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandProgress>))]
public enum CommandProgress
{
    /// <summary>Nothing is in flight.</summary>
    Idle,

    /// <summary>A command has been sent and no result has come back.</summary>
    Pending,

    /// <summary>The last command finished cleanly.</summary>
    Completed,

    /// <summary>The last command failed or was refused.</summary>
    Failed,

    /// <summary>The last command finished without establishing what the hardware did.</summary>
    Uncertain,
}

/// <summary>
/// WSGM's view of one capability: what the plugin last reported, plus what WSGM wants and is doing
/// about it.
/// </summary>
/// <remarks>
/// The split from <see cref="CapabilityState"/> is the point. The plugin owns observation; WSGM owns
/// intent. Keeping intent out of the plugin's message is what stops a device that happens to boot at
/// 15 W from being treated as though the user chose 15 W.
/// </remarks>
public sealed record CapabilityProjection
{
    /// <summary>The last state the plugin reported.</summary>
    public required CapabilityState State { get; init; }

    /// <summary>The value WSGM wants, or null when no layer supplies one.</summary>
    public CapabilityValue? DesiredValue { get; init; }

    /// <summary>Which layer supplied <see cref="DesiredValue"/>.</summary>
    public DeviceDesiredValueSource DesiredSource { get; init; } = DeviceDesiredValueSource.None;

    /// <summary>The value of an in-flight request, shown while a command is pending.</summary>
    public CapabilityValue? PendingValue { get; init; }

    /// <summary>Where the UI is in the request cycle.</summary>
    public CommandProgress Progress { get; init; } = CommandProgress.Idle;

    /// <summary>
    /// Whether the desired value is outside what the current descriptor accepts.
    /// </summary>
    /// <remarks>
    /// Set after a descriptor generation change narrowed a range. The persisted value is kept rather
    /// than clamped: silently moving a user's 30 W request to 25 W because firmware changed would be
    /// a decision made on their behalf and never surfaced.
    /// </remarks>
    public bool DesiredValueOutOfRange { get; init; }
}
