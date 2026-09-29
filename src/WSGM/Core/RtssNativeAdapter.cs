using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Production RTSS adapter over the installed architecture-matched profile API.</summary>
internal sealed class RtssNativeAdapter : IRtssAdapter
{
    private const string FrameLimitProperty = "FramerateLimit";
    private const string OverlayEnabledProperty = "EnableOSD";
    // Steam's selector runs OFF plus 1..4, all rendered by WSGM's own OSD slot: 1..3 are the
    // fixed presets (HandheldCompanion's structure, fed from RTSS's LibreHardwareMonitor
    // provider) and 4 is the user-configured Custom layout from WSGM's Settings — HC's Custom
    // level. EnableOSD is only the RTSS presentation gate: a nonzero WSGM level sets it to one in
    // the global and current profiles, while zero only clears WSGM's slot. Writing EnableOSD=0
    // here would disable external feeders too — the field regression that killed every overlay on
    // the reference device on 2026-09-01.
    private const int MaximumOverlayLevel = 4;
    private readonly RtssDiscovery _discovery;
    private readonly RtssOsdRenderer _osd;
    private RtssProfileApi? _api;
    private RtssProbe? _lastProbe;
    private long _generation;
    private bool _disposed;

    internal RtssNativeAdapter(RtssDiscovery? discovery = null)
    {
        _discovery = discovery ?? new RtssDiscovery();
        // The renderer's sensor source starts RTSS's LHM provider on demand, which needs the
        // installation directory the last probe verified.
        _osd = new RtssOsdRenderer(() => _lastProbe?.ExecutablePath);
    }

    /// <inheritdoc/>
    public void ApplyOsdCustomization(RtssOsdCustomSettings settings) => _osd.ApplyCustom(settings);

    /// <inheritdoc/>
    /// <remarks>
    /// Answered from the installation's <c>Profiles</c> directory rather than the profile API,
    /// because the API's LoadProfile cannot distinguish "absent" from "present with defaults" —
    /// and SaveProfile on an absent name is precisely the creation this check exists to avoid.
    /// </remarks>
    public bool ProfileExists(string rtssProfileName)
    {
        if (rtssProfileName.Length == 0)
        {
            return true;
        }

        string? directory = _lastProbe?.ExecutablePath is { } executable
            ? Path.GetDirectoryName(executable)
            : null;
        return directory is not null
            && File.Exists(Path.Combine(directory, "Profiles", rtssProfileName + ".cfg"));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every entry point of this adapter runs on the thread pool. All of the work below is
    /// synchronous — registry reads, filesystem and signature checks, PE-export inspection, process
    /// enumeration, and the profile API's own blocking calls — and the callers reach it from a
    /// completed semaphore wait on an overlay or QAM click handler, which is the Avalonia UI
    /// thread. Without the hop, interacting with a performance control froze the UI for as long as
    /// discovery took.
    /// </remarks>
    public Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => ProbeCore(cancellationToken), cancellationToken);
    }

    private RtssProbe ProbeCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RtssProbe probe = _discovery.Probe();
        if (probe.Availability != RtssAvailability.AdapterUnavailable
            || probe.ExecutablePath is null)
        {
            ReleaseApi();
            _lastProbe = probe;
            return probe;
        }

        try
        {
            EnsureApi(probe);
            RtssProbe ready = probe with
            {
                Availability = RtssAvailability.Ready,
                Capabilities = new RtssCapabilities(
                    0,
                    1000,
                    new HashSet<int> { 0, 1, 2, 3, MaximumOverlayLevel },
                    FrameLimitReadback: true,
                    OverlayLevelReadback: true),
                Diagnostic = "RTSS profile API is ready.",
            };
            _lastProbe = ready;
            return ready;
        }
        catch (Exception ex)
        {
            ReleaseApi();
            RtssProbe degraded = probe with
            {
                Availability = RtssAvailability.Degraded,
                Diagnostic = $"RTSS profile API load failed: {ex.Message}",
            };
            _lastProbe = degraded;
            return degraded;
        }
    }

    public async Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        long generation,
        CancellationToken cancellationToken)
    {
        await RequireReadyAsync(generation, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => ReadCore(rtssProfileName),
            cancellationToken).ConfigureAwait(false);
    }

    private RtssReadback ReadCore(string rtssProfileName)
    {
        RtssProfileApi api = _api
            ?? throw new InvalidOperationException("RTSS profile API is not loaded.");
        api.LoadProfile(rtssProfileName);
        if (!api.TryGetUInt32(FrameLimitProperty, out uint frameLimit)
            || frameLimit > int.MaxValue)
        {
            throw new InvalidDataException("RTSS did not return a valid frame-limit value.");
        }

        // The overlay level is WSGM-owned renderer state, not an RTSS property; reading this
        // process's own live level is the verified readback.
        return new RtssReadback(
            new PerformanceValues((int)frameLimit, _osd.Level),
            PerformanceReadbackQuality.Verified,
            PerformanceReadbackQuality.Verified,
            DateTimeOffset.UtcNow);
    }

    public async Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Control is PerformanceControl.FrameLimit
            && request.Value is < 0 or > 1000)
        {
            return new(false, "The frame-limit value is outside the verified RTSS range.");
        }

        if (request.Control is PerformanceControl.OverlayLevel
            && request.Value is < 0 or > MaximumOverlayLevel)
        {
            return new(false, "The overlay level is outside the supported range.");
        }

        await RequireReadyAsync(request.Generation, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ApplyCore(request), cancellationToken).ConfigureAwait(false);
    }

    private RtssApplyResult ApplyCore(RtssApplyRequest request)
    {
        RtssProfileApi api = _api
            ?? throw new InvalidOperationException("RTSS profile API is not loaded.");
        if (request.Control is PerformanceControl.OverlayLevel)
        {
            IReadOnlyList<string> activationProfiles = OverlayActivationProfiles(
                request.Value,
                request.RtssProfileName);
            if (activationProfiles.Count > 0
                && !TryEnableOverlayPresentation(api, activationProfiles, out string? refusal))
            {
                return new(false, refusal);
            }

            _osd.SetLevel(request.Value);
            return new(true, null);
        }

        api.LoadProfile(request.RtssProfileName);
        (string property, uint propertyValue) = request.Control switch
        {
            PerformanceControl.FrameLimit =>
                (FrameLimitProperty, checked((uint)request.Value)),
            _ => (string.Empty, 0u),
        };
        if (property.Length == 0 || !api.TrySetUInt32(property, propertyValue))
        {
            return new(false, "RTSS rejected the performance-profile value.");
        }

        api.SaveProfile(request.RtssProfileName);
        api.UpdateProfiles();
        return new(true, null);
    }

    private static bool TryEnableOverlayPresentation(
        RtssProfileApi api,
        IReadOnlyList<string> profiles,
        out string? refusal)
    {
        bool changed = false;
        List<string> changedProfiles = [];
        foreach (string profile in profiles)
        {
            api.LoadProfile(profile);
            if (api.TryGetUInt32(OverlayEnabledProperty, out uint enabled) && enabled == 1)
            {
                continue;
            }

            if (!api.TrySetUInt32(OverlayEnabledProperty, 1))
            {
                refusal = profile.Length == 0
                    ? "RTSS rejected enabling OSD presentation in the global profile."
                    : $"RTSS rejected enabling OSD presentation for '{profile}'.";
                return false;
            }

            api.SaveProfile(profile);
            changed = true;
            changedProfiles.Add(profile);
        }

        if (changed)
        {
            api.UpdateProfiles();
        }

        string requested = ProfileLabels(profiles);
        Log.Change(
            "rtss.overlay-presentation",
            changedProfiles.Count == 0
                ? $"RTSS OSD presentation already enabled: profiles={requested}."
                : $"RTSS OSD presentation enabled: profiles={requested}; "
                    + $"changed={ProfileLabels(changedProfiles)}.");

        refusal = null;
        return true;
    }

    private static string ProfileLabel(string profile) => profile.Length == 0
        ? "<global>"
        : profile;

    private static string ProfileLabels(IReadOnlyList<string> profiles)
    {
        string[] labels = new string[profiles.Count];
        for (int index = 0; index < profiles.Count; index++)
        {
            labels[index] = ProfileLabel(profiles[index]);
        }

        return string.Join(", ", labels);
    }

    /// <summary>Profiles whose RTSS presentation gate a nonzero WSGM overlay must open.</summary>
    /// <remarks>
    /// Global covers applications without an explicit profile; the current executable is added
    /// because an explicit per-app `EnableOSD=0` overrides global state. The service re-applies on
    /// every application transition, so each profile is repaired when it becomes the active target.
    /// </remarks>
    internal static IReadOnlyList<string> OverlayActivationProfiles(
        int overlayLevel,
        string requestedProfile) => overlayLevel <= 0
            ? []
            : string.IsNullOrEmpty(requestedProfile)
                ? [string.Empty]
                : [requestedProfile, string.Empty];

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _osd.Dispose();
            ReleaseApi();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<RtssProbe> RequireReadyAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        RtssProbe probe = _lastProbe is { Availability: RtssAvailability.Ready } cached
            && cached.Generation == generation
            ? cached
            : await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probe.Availability != RtssAvailability.Ready || probe.Generation != generation)
        {
            throw new InvalidOperationException(
                "RTSS availability or process generation changed before the operation.");
        }

        return probe;
    }

    private void EnsureApi(RtssProbe probe)
    {
        if (_api is not null && _generation == probe.Generation)
        {
            return;
        }

        ReleaseApi();
        string executable = probe.ExecutablePath
            ?? throw new InvalidDataException("RTSS discovery returned no executable path.");
        string directory = Path.GetDirectoryName(executable)
            ?? throw new InvalidDataException("RTSS executable has no installation directory.");
        string library = Path.Combine(
            directory,
            Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll");
        _api = new RtssProfileApi(library);
        _generation = probe.Generation;
    }

    private void ReleaseApi()
    {
        _api?.Dispose();
        _api = null;
        _generation = 0;
        _lastProbe = null;
    }
}

/// <summary>In-memory RTSS adapter used only by the safe overlay-test mode.</summary>
internal sealed class SimulatedRtssAdapter : IRtssAdapter
{
    /// <inheritdoc/>
    public void ApplyOsdCustomization(RtssOsdCustomSettings settings)
    {
        // No renderer here; the simulated adapter never draws.
    }

    /// <inheritdoc/>
    public bool ProfileExists(string rtssProfileName) =>
        rtssProfileName.Length == 0 || _profiles.ContainsKey(rtssProfileName);

    private static readonly RtssCapabilities Capabilities = new(
        0,
        240,
        new HashSet<int> { 0, 1, 2, 3, 4 },
        FrameLimitReadback: true,
        OverlayLevelReadback: true);
    private readonly Dictionary<string, PerformanceValues> _profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new PerformanceValues(60, 2),
        };
    private bool _disposed;

    public Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(new RtssProbe(
            RtssAvailability.Ready,
            "overlay-test",
            null,
            1,
            Capabilities,
            "Simulated RTSS state; no external process or profile is accessed."));
    }

    public Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (generation != 1)
        {
            throw new InvalidOperationException("Simulated RTSS generation changed.");
        }

        PerformanceValues values = _profiles.TryGetValue(rtssProfileName, out PerformanceValues? profile)
            && profile is not null
            ? profile
            : _profiles[string.Empty];
        return Task.FromResult(new RtssReadback(
            values,
            PerformanceReadbackQuality.Verified,
            PerformanceReadbackQuality.Verified,
            DateTimeOffset.UtcNow));
    }

    public Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Generation != 1 || !Capabilities.IsValid(request.Control, request.Value))
        {
            return Task.FromResult(new RtssApplyResult(false, "Simulated request is invalid."));
        }

        PerformanceValues current = _profiles.TryGetValue(
            request.RtssProfileName,
            out PerformanceValues? profile)
            && profile is not null
            ? profile
            : _profiles[string.Empty];
        _profiles[request.RtssProfileName] = current.With(request.Control, request.Value);
        return Task.FromResult(new RtssApplyResult(true, null));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _profiles.Clear();
        return ValueTask.CompletedTask;
    }
}
