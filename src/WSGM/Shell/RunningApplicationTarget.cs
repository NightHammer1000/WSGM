using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Truthful availability of the canonical running-application target.</summary>
internal enum RunningApplicationTargetState
{
    Global,
    Active,
    IdentityOnly,
    Ambiguous,
    Unavailable,
}

/// <summary>
/// Canonical running-application identity shared by controller and performance policy clients.
/// </summary>
internal sealed record RunningApplicationTargetSnapshot(
    long Generation,
    long SourceGeneration,
    RunningApplicationTargetState State,
    string? ApplicationId,
    uint? SteamAppId,
    string? ExecutablePath,
    string? RtssProfileName,
    DateTimeOffset ObservedAt,
    string? Diagnostic)
{
    internal static RunningApplicationTargetSnapshot Initial(DateTimeOffset observedAt) => new(
        0,
        0,
        RunningApplicationTargetState.Unavailable,
        null,
        null,
        null,
        null,
        observedAt,
        "Running-application observation has not started.");
}

/// <summary>Bounded raw running-AppID observation from Steam.</summary>
internal sealed record SteamRunningAppObservation(
    bool Reachable,
    IReadOnlyList<uint> AppIds,
    long SourceGeneration,
    string? Diagnostic);

/// <summary>Optional executable/profile resolution for one known Steam AppID.</summary>
/// <param name="ExecutablePath">The shortcut target, when Steam exposes one.</param>
/// <param name="RtssProfileName">The executable file name RTSS keys its profile on.</param>
/// <param name="Diagnostic">Why the resolution is partial, for the log.</param>
/// <param name="InstallFolder">
/// A store title's install folder. Steam never exposes a store title's executable, so the folder is
/// what the foreground pairing is validated against: only a process running from inside it may
/// become the game's RTSS profile.
/// </param>
internal sealed record SteamRunningAppProfile(
    string? ExecutablePath,
    string? RtssProfileName,
    string? Diagnostic,
    string? InstallFolder = null);

/// <summary>
/// The application the user currently has in front of them, independent of Steam.
/// </summary>
/// <param name="ExecutableName">
/// File name of the foreground process with its extension, or <see langword="null"/> when nothing
/// usable is in front.
/// </param>
/// <param name="ExecutablePath">
/// Full image path of the same process, when it could be read. The projection needs it to prove a
/// candidate actually runs from a Steam title's install folder before pairing the two.
/// </param>
/// <remarks>
/// This is the second identity source, and it exists so per-application policy works outside a
/// Steam game: on the desktop, for a title launched from another launcher, or for anything the user
/// picks a profile for from the overlay. It never competes with Steam — see
/// <see cref="RunningApplicationTargetProjection"/> for the precedence rule.
/// </remarks>
internal sealed record ForegroundApplicationObservation(
    string? ExecutableName,
    string? ExecutablePath = null)
{
    /// <summary>Nothing usable in the foreground.</summary>
    internal static ForegroundApplicationObservation None { get; } = new((string?)null);
}

/// <summary>Pure projection that never carries a previous application's identity forward.</summary>
/// <remarks>
/// Two identity sources, one answer. Steam wins whenever it names exactly one running application,
/// because that identity is the one its own launch went through and the one the shortcut's
/// executable was resolved from; the foreground window can only ever agree with it or be wrong
/// about it. The foreground fills every case where Steam names nothing — the desktop, another
/// launcher, a title started outside Steam — which is the whole reason it exists.
/// <para>
/// Deliberately not a tie-break: when Steam reports more than one running application it stays
/// ambiguous rather than letting the foreground pick a winner. The foreground says which window has
/// focus, not which of two running games the user means to configure, and quietly choosing one
/// would write a power limit against the other.
/// </para>
/// </remarks>
internal static class RunningApplicationTargetProjection
{
    internal static RunningApplicationTargetSnapshot Apply(
        RunningApplicationTargetSnapshot current,
        SteamRunningAppObservation observation,
        SteamRunningAppProfile? profile,
        DateTimeOffset observedAt,
        ForegroundApplicationObservation? foreground = null)
    {
        RunningApplicationTargetSnapshot candidate = Project(observation, profile, observedAt);
        candidate = ApplyForeground(current, candidate, profile, foreground);
        if (Equivalent(current, candidate))
        {
            return current with { ObservedAt = observedAt };
        }

        return candidate with { Generation = current.Generation + 1 };
    }

    /// <summary>
    /// Supplies the executable profile Steam omitted without replacing Steam's canonical identity.
    /// </summary>
    private static RunningApplicationTargetSnapshot ApplyForeground(
        RunningApplicationTargetSnapshot current,
        RunningApplicationTargetSnapshot steam,
        SteamRunningAppProfile? profile,
        ForegroundApplicationObservation? foreground)
    {
        if (steam.State is RunningApplicationTargetState.IdentityOnly
            && current.State is RunningApplicationTargetState.Active
            && current.SteamAppId == steam.SteamAppId
            && string.Equals(current.ApplicationId, steam.ApplicationId, StringComparison.Ordinal)
            && current.RtssProfileName is { Length: > 0 })
        {
            // Steam does not expose a launch executable for ordinary store applications. Once one
            // has been supplied, keep the pairing across focus changes — an alt-tab changes focus,
            // not the game whose per-application policy is active. A DIFFERENT executable proven to
            // run from the game's own install folder may still take the pairing over: that is a
            // launcher handing off to the real game process.
            if (ValidatedGameExecutable(profile, foreground) is { } handoff
                && !string.Equals(
                    handoff.Name,
                    current.RtssProfileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return steam with
                {
                    State = RunningApplicationTargetState.Active,
                    ExecutablePath = handoff.Path,
                    RtssProfileName = handoff.Name,
                    Diagnostic = null,
                };
            }

            return steam with
            {
                State = RunningApplicationTargetState.Active,
                ExecutablePath = current.ExecutablePath,
                RtssProfileName = current.RtssProfileName,
                Diagnostic = null,
            };
        }

        if (steam.State is not (RunningApplicationTargetState.Global
            or RunningApplicationTargetState.IdentityOnly)
            || foreground?.ExecutableName is not { Length: > 0 } executable)
        {
            return steam;
        }

        // Ambiguous must stay ambiguous, and Unavailable means the Steam observation itself failed,
        // where publishing an identity would claim knowledge WSGM does not have.
        string profileName = executable.Trim();
        if (ForegroundApplicationFilter.Classify(profileName)
                is not ForegroundApplicationKind.Application
            || profileName.Length > 128
            || !profileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return steam;
        }

        if (steam.State is RunningApplicationTargetState.IdentityOnly)
        {
            // Steam's own game. The foreground is whatever the user happens to have in focus — a
            // terminal, a browser — so a bare name must never become the game's profile: that
            // pairing is sticky for the whole run, and WindowsTerminal.exe captured HITMAN 3's
            // frame limit exactly this way (device-observed 2026-09-02). A store title's install
            // folder is known, so only an executable proven to run from it may pair. A shortcut has
            // no folder to check and its target resolution already names the executable, so its
            // rare unresolved case keeps the name-based fill.
            if (steam.SteamAppId is { } appId
                && !SteamRunningApplicationProbe.IsShortcutAppId(appId))
            {
                if (ValidatedGameExecutable(profile, foreground) is not { } game)
                {
                    return steam;
                }

                return steam with
                {
                    State = RunningApplicationTargetState.Active,
                    ExecutablePath = game.Path,
                    RtssProfileName = game.Name,
                    Diagnostic = null,
                };
            }

            return steam with
            {
                State = RunningApplicationTargetState.Active,
                ExecutablePath = null,
                RtssProfileName = profileName,
                Diagnostic = null,
            };
        }

        return steam with
        {
            State = RunningApplicationTargetState.Active,
            ApplicationId = $"process:{profileName.ToLowerInvariant()}",
            SteamAppId = null,
            ExecutablePath = null,
            RtssProfileName = profileName,
            Diagnostic = null,
        };
    }

    /// <summary>
    /// The foreground executable, if and only if it provably runs from the game's install folder.
    /// </summary>
    private static (string Name, string Path)? ValidatedGameExecutable(
        SteamRunningAppProfile? profile,
        ForegroundApplicationObservation? foreground)
    {
        if (profile?.InstallFolder is not { Length: > 0 } folder
            || foreground?.ExecutablePath is not { Length: > 0 } path)
        {
            return null;
        }

        string name = (foreground.ExecutableName ?? string.Empty).Trim();
        if (ForegroundApplicationFilter.Classify(name) is not ForegroundApplicationKind.Application
            || name.Length is 0 or > 128
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string fullPath;
        string prefix;
        try
        {
            fullPath = Path.GetFullPath(path);
            string fullFolder = Path.GetFullPath(folder);
            prefix = fullFolder.EndsWith(Path.DirectorySeparatorChar)
                ? fullFolder
                : fullFolder + Path.DirectorySeparatorChar;
        }
        catch (Exception)
        {
            // An unparsable path proves nothing; without proof there is no pairing.
            return null;
        }

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? (name, fullPath)
            : null;
    }

    private static RunningApplicationTargetSnapshot Project(
        SteamRunningAppObservation observation,
        SteamRunningAppProfile? profile,
        DateTimeOffset observedAt)
    {
        if (!observation.Reachable)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Unavailable,
                null,
                null,
                null,
                null,
                observedAt,
                Bound(observation.Diagnostic ?? "Steam running-app state is unavailable."));
        }

        uint[] appIds = observation.AppIds.Distinct().Take(3).ToArray();
        if (appIds.Length == 0)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Global,
                null,
                null,
                null,
                null,
                observedAt,
                null);
        }

        if (appIds.Length != 1)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Ambiguous,
                null,
                null,
                null,
                null,
                observedAt,
                "Steam reports more than one running AppID; global policy remains active.");
        }

        uint appId = appIds[0];
        bool profileResolved = !string.IsNullOrWhiteSpace(profile?.RtssProfileName);
        return new RunningApplicationTargetSnapshot(
            0,
            observation.SourceGeneration,
            profileResolved
                ? RunningApplicationTargetState.Active
                : RunningApplicationTargetState.IdentityOnly,
            $"steam:{appId}",
            appId,
            profile?.ExecutablePath,
            profile?.RtssProfileName,
            observedAt,
            Bound(profile?.Diagnostic));
    }

    private static bool Equivalent(
        RunningApplicationTargetSnapshot left,
        RunningApplicationTargetSnapshot right) =>
        left.State == right.State
        && left.SourceGeneration == right.SourceGeneration
        && string.Equals(left.ApplicationId, right.ApplicationId, StringComparison.Ordinal)
        && left.SteamAppId == right.SteamAppId
        && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.RtssProfileName, right.RtssProfileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Diagnostic, right.Diagnostic, StringComparison.Ordinal);

    private static string? Bound(string? value) => value is null || value.Length <= 1024
        ? value
        : value[..1024] + "...";
}

/// <summary>
/// Uses Steam's AppLifetime notification to retain a running-AppID set inside SharedJSContext.
/// The managed side only reads the bounded set and never infers application changes from focus.
/// </summary>
internal sealed class SteamRunningApplicationProbe
{
    private const uint ShortcutAppIdFloor = 0x80000000;
    private static readonly TimeSpan EvaluationBudget = TimeSpan.FromSeconds(4);

    // Steam's live UI store uses display_status 4 for the initial running set. AppLifetime
    // notifications own every transition after that seed; focused-window stores are not consulted.
    private const string ObserveExpression =
        "(()=>{try{" +
        "window.__wsgm=window.__wsgm||{};const W=window.__wsgm;" +
        "if(!W.runningAppsV1){" +
        "const ids=new Set((window.appStore&&appStore.allApps||[])" +
        ".filter(a=>Number(a.display_status)===4).map(a=>Number(a.appid))" +
        ".filter(a=>Number.isInteger(a)&&a>0&&a<=4294967295));" +
        "const R={ids:ids,gen:1,dispose:()=>{}};W.runningAppsV1=R;" +
        "const h=SteamClient.GameSessions.RegisterForAppLifetimeNotifications(e=>{" +
        "const id=Number(e&&e.unAppID);if(!Number.isInteger(id)||id<=0||id>4294967295)return;" +
        "const before=ids.size;if(e.bRunning)ids.add(id);else ids.delete(id);" +
        "if(ids.size!==before)R.gen++;});" +
        "R.dispose=()=>{try{h.unregister();}catch(_){}};}" +
        "const R=W.runningAppsV1;return JSON.stringify({ok:true,ids:[...R.ids].slice(0,3)," +
        "ambiguous:R.ids.size>1,generation:R.gen});" +
        "}catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

    private const string RemoveExpression =
        "(()=>{try{const W=window.__wsgm;if(W&&W.runningAppsV1){" +
        "W.runningAppsV1.dispose();delete W.runningAppsV1;}" +
        "return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false});}})()";

    private readonly ISteamUiTransport _transport;

    internal SteamRunningApplicationProbe(ISteamUiTransport transport)
        => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>Whether Steam models the AppID as a non-Steam shortcut.</summary>
    internal static bool IsShortcutAppId(uint appId) => appId >= ShortcutAppIdFloor;

    public async ValueTask<IAsyncDisposable> SubscribeAsync(CancellationToken cancellationToken)
    {
        IAsyncDisposable transportLease = await _transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext,
            cancellationToken).ConfigureAwait(false);
        return new ProbeLease(_transport, transportLease);
    }

    public async Task<SteamRunningAppObservation> ObserveAsync(
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            ObserveExpression,
            EvaluationBudget,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            // The CEF master switch disables only Steam-backed identity. Foreground observation is
            // independent and must keep driving per-application policy for non-Steam games, so a
            // deliberate disable projects as "Steam names no app" rather than as a transport
            // failure that suppresses the foreground fallback.
            if (string.Equals(
                    result.Error,
                    "Steam CEF integration disabled in settings.",
                    StringComparison.Ordinal))
            {
                return new SteamRunningAppObservation(true, [], 0, null);
            }
            return new SteamRunningAppObservation(
                false,
                [],
                0,
                result.Error ?? "Steam SharedJSContext is unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("ok", out JsonElement ok)
                || ok.ValueKind != JsonValueKind.True)
            {
                string? error = root.TryGetProperty("err", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : "Steam rejected the running-app observer.";
                return new SteamRunningAppObservation(false, [], 0, error);
            }

            List<uint> appIds = [];
            if (root.TryGetProperty("ids", out JsonElement ids)
                && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in ids.EnumerateArray().Take(3))
                {
                    if (id.TryGetUInt32(out uint appId) && appId > 0)
                    {
                        appIds.Add(appId);
                    }
                }
            }

            long sourceGeneration = root.TryGetProperty("generation", out JsonElement generation)
                && generation.TryGetInt64(out long parsedGeneration)
                ? parsedGeneration
                : 0;
            return new SteamRunningAppObservation(true, appIds, sourceGeneration, null);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppObservation(
                false,
                [],
                0,
                $"Steam running-app payload was invalid: {ex.Message}");
        }
    }

    public async Task<SteamRunningAppProfile> ResolveProfileAsync(
        uint steamAppId,
        CancellationToken cancellationToken)
    {
        // One details read serves both kinds of entry: a shortcut names its target executable, and
        // a store title names only its install folder — Steam never exposes a store title's
        // executable, so the folder is what foreground pairing is validated against.
        string expression =
            "(async()=>{try{const d=await new Promise(res=>{let t;try{" +
            "const h=SteamClient.Apps.RegisterForAppDetails(" + steamAppId + ",d=>{" +
            "clearTimeout(t);try{h.unregister();}catch(_){}res(d);});" +
            "t=setTimeout(()=>{try{h.unregister();}catch(_){}res(null);},3000);" +
            "}catch(_){res(null);}});return JSON.stringify({ok:!!d,exe:d&&d.strShortcutExe||''," +
            "dir:d&&d.strInstallFolder||''});" +
            "}catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        SteamUiEvaluationResult result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            EvaluationBudget,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamRunningAppProfile(null, null, result.Error);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            string target = root.TryGetProperty("exe", out JsonElement executable)
                && executable.ValueKind == JsonValueKind.String
                ? executable.GetString() ?? string.Empty
                : string.Empty;
            string folder = root.TryGetProperty("dir", out JsonElement installFolder)
                && installFolder.ValueKind == JsonValueKind.String
                ? installFolder.GetString() ?? string.Empty
                : string.Empty;
            return IsShortcutAppId(steamAppId)
                ? NormalizeShortcutTarget(target)
                : NormalizeInstallFolder(folder);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(
                null,
                null,
                $"Steam app details were invalid: {ex.Message}");
        }
    }

    /// <summary>Turns a store title's reported install folder into pairing evidence.</summary>
    /// <param name="folder">The <c>strInstallFolder</c> value Steam reported.</param>
    internal static SteamRunningAppProfile NormalizeInstallFolder(string folder)
    {
        folder = folder.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam did not report the running title's install folder; "
                + "RTSS remains on the global profile.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam reported an install folder that is not an absolute path.");
            }

            string normalized = Path.GetFullPath(folder);
            if (!Directory.Exists(normalized))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam's reported install folder is not present.");
            }

            return new SteamRunningAppProfile(
                null,
                null,
                "Steam exposes no executable for a store title; the RTSS profile pairs with the "
                + "foreground process running from its install folder.",
                normalized);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(null, null, ex.Message);
        }
    }

    internal static SteamRunningAppProfile NormalizeShortcutTarget(string target)
    {
        target = target.Trim();
        if (target.Length >= 2 && target[0] == '"' && target[^1] == '"')
        {
            target = target[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam did not expose the running shortcut's executable.");
        }

        string profileName;
        string normalizedPath;
        try
        {
            if (!Path.IsPathFullyQualified(target))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam reported a shortcut target that is not an absolute path.");
            }
            normalizedPath = Path.GetFullPath(target);
            profileName = Path.GetFileName(normalizedPath);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(null, null, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(profileName)
            || profileName.Length > 128
            || !profileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || profileName.StartsWith("WSGM.Launch", StringComparison.OrdinalIgnoreCase))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "The shortcut target is not a truthful RTSS application profile.");
        }

        if (!File.Exists(normalizedPath))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam's shortcut executable is no longer present.");
        }

        return new SteamRunningAppProfile(normalizedPath, profileName, null);
    }

    private sealed class ProbeLease(
        ISteamUiTransport transport,
        IAsyncDisposable transportLease) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await transport.EvaluateAsync(
                    SteamUiTargetRole.SharedJsContext,
                    RemoveExpression,
                    EvaluationBudget,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam running-app observer cleanup failed: {ex.Message}");
            }
            finally
            {
                await transportLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Session-owned, consumer-aware running-application monitor. It polls only the event-maintained
/// bounded snapshot while observed and publishes global/unknown immediately on exit or failure.
/// </summary>
internal sealed class RunningApplicationMonitor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProfileRetryInterval = TimeSpan.FromSeconds(10);
    private readonly SteamRunningApplicationProbe _probe;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _observerSignal = new(0, 1);
    private readonly object _stateGate = new();
    private readonly Task _loop;
    private RunningApplicationTargetSnapshot _current;
    private SteamRunningAppProfile? _profile;
    private uint? _profileAppId;
    private DateTimeOffset _nextProfileRetry;
    private SteamRunningAppObservation? _lastObservation;
    private ForegroundApplicationObservation _foreground = ForegroundApplicationObservation.None;
    private int _observerCount;
    private long _steamEnableGeneration;
    private volatile bool _steamEnabled;
    private bool _disposed;

    internal RunningApplicationMonitor(
        SteamRunningApplicationProbe probe,
        bool steamEnabled)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _steamEnabled = steamEnabled;
        _current = RunningApplicationTargetSnapshot.Initial(DateTimeOffset.UtcNow);
        _loop = Task.Run(ObserveLoopAsync);
    }

    internal event Action<RunningApplicationTargetSnapshot>? Changed;

    internal RunningApplicationTargetSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    /// <summary>Reports the application the user brought to the foreground.</summary>
    /// <param name="executableName">Foreground executable file name, or null for none.</param>
    /// <param name="executablePath">Full image path of the same process, when readable.</param>
    /// <remarks>
    /// Still one monitor and one projection: the foreground is an input to the same projection, not
    /// a second observer publishing its own answer. It republishes against the last Steam
    /// observation rather than re-reading Steam, because re-reading here would be exactly the
    /// second CEF poll this class exists to avoid — and it would run on whatever thread the window
    /// hook fired on.
    /// </remarks>
    internal void ReportForeground(string? executableName, string? executablePath = null)
    {
        SteamRunningAppObservation? observation;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            ForegroundApplicationObservation next = new(executableName, executablePath);
            if (string.Equals(
                    _foreground.ExecutableName,
                    next.ExecutableName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    _foreground.ExecutablePath,
                    next.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _foreground = next;
            observation = _lastObservation;
        }

        if (observation is null)
        {
            // Nothing has been observed from Steam yet, so there is no snapshot to re-project
            // against. The next poll picks the foreground up from the field.
            Log.Info(
                $"Foreground application {executableName ?? "(none)"} recorded before the first "
                + "Steam observation; it applies at the next poll.");
            return;
        }

        Publish(observation, _profile);
    }

    internal IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Increment(ref _observerCount) == 1)
        {
            TrySignalObserver();
        }
        return new ObservationLease(this);
    }

    /// <summary>Starts or stops the Steam-backed identity source without stopping foreground policy.</summary>
    /// <param name="enabled">Whether the CEF-backed source may subscribe and poll.</param>
    internal void SetSteamEnabled(bool enabled)
    {
        if (_steamEnabled == enabled || _disposed)
        {
            return;
        }

        _steamEnabled = enabled;
        Interlocked.Increment(ref _steamEnableGeneration);
        if (!enabled)
        {
            // An intentional CEF disable means Steam contributes no identity; the foreground
            // source remains authoritative for non-Steam and desktop applications.
            Publish(new SteamRunningAppObservation(true, [], 0, null), null);
        }
        TrySignalObserver();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        TrySignalObserver();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _observerSignal.Dispose();
        _shutdown.Dispose();
    }

    private async Task ObserveLoopAsync()
    {
        CancellationToken cancellationToken = _shutdown.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _observerCount) == 0)
            {
                await _observerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!_steamEnabled)
            {
                await _observerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            IAsyncDisposable subscription;
            try
            {
                subscription = await _probe.SubscribeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _profileAppId = null;
                _profile = null;
                _nextProfileRetry = default;
                Publish(new SteamRunningAppObservation(false, [], 0, ex.Message), null);
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await using (subscription)
            {
                while (_steamEnabled
                    && Volatile.Read(ref _observerCount) > 0
                    && !cancellationToken.IsCancellationRequested)
                {
                    await ObserveOnceAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ObserveOnceAsync(CancellationToken cancellationToken)
    {
        long enabledGeneration = Interlocked.Read(ref _steamEnableGeneration);
        if (!_steamEnabled)
        {
            return;
        }

        SteamRunningAppObservation observation;
        try
        {
            observation = await _probe.ObserveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            observation = new SteamRunningAppObservation(false, [], 0, ex.Message);
        }

        if (!_steamEnabled || enabledGeneration != Interlocked.Read(ref _steamEnableGeneration))
        {
            return;
        }

        uint? singleAppId = observation.Reachable && observation.AppIds.Distinct().Take(2).ToArray()
            is [uint appId]
            ? appId
            : null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ShouldResolveProfile(singleAppId, _profileAppId, _profile, now, _nextProfileRetry))
        {
            _profileAppId = singleAppId;
            _profile = singleAppId is { } value
                ? await ResolveProfileAsync(value, cancellationToken).ConfigureAwait(false)
                : null;
            if (!_steamEnabled || enabledGeneration != Interlocked.Read(ref _steamEnableGeneration))
            {
                return;
            }
            _nextProfileRetry = singleAppId is { } resolvedId
                && ProfileUnresolved(resolvedId, _profile)
                ? now + ProfileRetryInterval
                : default;
        }

        if (_steamEnabled && enabledGeneration == Interlocked.Read(ref _steamEnableGeneration))
        {
            Publish(observation, _profile);
        }
    }

    /// <summary>Decides when an AppID needs a fresh executable or install-folder lookup.</summary>
    internal static bool ShouldResolveProfile(
        uint? observedAppId,
        uint? resolvedAppId,
        SteamRunningAppProfile? profile,
        DateTimeOffset now,
        DateTimeOffset retryAt) =>
        observedAppId != resolvedAppId
        || (observedAppId is { } appId
            && ProfileUnresolved(appId, profile)
            && now >= retryAt);

    /// <summary>Whether resolution is still missing what pairing needs for this kind of entry.</summary>
    /// <remarks>
    /// A shortcut resolves to its target executable; a store title resolves to its install folder,
    /// because Steam never exposes a store title's executable. Each kind retries only its own
    /// missing answer — a resolved store title must not re-query every interval merely because its
    /// profile name legitimately stays empty.
    /// </remarks>
    private static bool ProfileUnresolved(uint appId, SteamRunningAppProfile? profile) =>
        SteamRunningApplicationProbe.IsShortcutAppId(appId)
            ? string.IsNullOrWhiteSpace(profile?.RtssProfileName)
            : string.IsNullOrWhiteSpace(profile?.InstallFolder);

    private void Publish(
        SteamRunningAppObservation observation,
        SteamRunningAppProfile? profile)
    {
        RunningApplicationTargetSnapshot next;
        bool changed;
        string? foregroundName;
        lock (_stateGate)
        {
            _lastObservation = observation;
            foregroundName = _foreground.ExecutableName;
            next = RunningApplicationTargetProjection.Apply(
                _current,
                observation,
                profile,
                DateTimeOffset.UtcNow,
                _foreground);
            changed = next.Generation != _current.Generation;
            _current = next;
        }

        // Keyed on content, not on state transitions: a device where Steam forever reports an
        // empty running set never transitions, and that silence is exactly the observation that
        // diagnoses "per-game controls never appear".
        Log.Change(
            "running-apps.observation",
            $"Steam running-app observation: reachable={observation.Reachable}, "
                + $"ids=[{string.Join(",", observation.AppIds)}], "
                + $"generation={observation.SourceGeneration}, "
                + $"foreground={foregroundName ?? "-"}, projected={next.State}");
        if (!changed)
        {
            return;
        }

        LogTransition(next);
        try
        {
            Changed?.Invoke(next);
        }
        catch (Exception ex)
        {
            Log.Error("Running-application target observer failed", ex);
        }
    }

    private static void LogTransition(RunningApplicationTargetSnapshot target)
    {
        switch (target.State)
        {
            case RunningApplicationTargetState.Active when target.SteamAppId is null:
                // No AppID means the foreground supplied this identity, which is worth saying
                // outright: it is the difference between "Steam launched this" and "this is simply
                // what the user has in front of them".
                Log.Info(
                    $"Foreground application is active: {target.RtssProfileName}; "
                    + "Steam reports no running application.");
                break;
            case RunningApplicationTargetState.Active:
                Log.Info(
                    $"Running application started: Steam AppID {target.SteamAppId}; "
                    + $"RTSS profile {target.RtssProfileName}.");
                break;
            case RunningApplicationTargetState.IdentityOnly:
                Log.Info(
                    $"Running application started: Steam AppID {target.SteamAppId}; "
                    + "executable profile unavailable, global RTSS policy remains active.");
                break;
            case RunningApplicationTargetState.Global:
                Log.Info("Running application exited; global application policy is active.");
                break;
            case RunningApplicationTargetState.Ambiguous:
                Log.Warn("Steam reports multiple running AppIDs; global application policy is active.");
                break;
            case RunningApplicationTargetState.Unavailable:
                Log.Warn(
                    $"Running-application target unavailable; global application policy is active: "
                    + $"{target.Diagnostic}");
                break;
        }
    }

    private async Task<SteamRunningAppProfile> ResolveProfileAsync(
        uint appId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.ResolveProfileAsync(appId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(null, null, ex.Message);
        }
    }

    private void ReleaseObservation()
    {
        int remaining = Interlocked.Decrement(ref _observerCount);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _observerCount, 0);
        }
    }

    private void TrySignalObserver()
    {
        try
        {
            if (_observerSignal.CurrentCount == 0)
            {
                _observerSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class ObservationLease(RunningApplicationMonitor owner) : IDisposable
    {
        private RunningApplicationMonitor? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseObservation();
    }
}
