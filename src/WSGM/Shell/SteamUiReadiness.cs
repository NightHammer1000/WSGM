using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Single policy gate for autonomous CEF work during Steam startup.</summary>
/// <remarks>
/// Two mechanisms consult it. Operations that run once (tab sync, card reconcile, download
/// polling) wait through <see cref="RunWhenReadyAsync"/>. Everything that talks to Steam
/// continuously — the persistent transport behind the patch host, the running-application probe
/// and every static evaluator — is switched at the transport itself by
/// <see cref="TransportShouldBeOpen"/>, because a cold-starting Steam opens its CEF port seconds
/// before it has a Big Picture window, and the first connection would otherwise inject the whole
/// native-QAM patch set into that headless session. Device evidence for both dates is in
/// <c>docs\boot-and-shell.md</c>.
/// </remarks>
internal static class SteamUiReadiness
{
    /// <summary>How often the shell re-reads the Big Picture window while it owns the transport
    /// gate. One second bounds the cold-start delay after the window appears; the boot splash keeps
    /// its own tighter detection because that one drives a visible fade.</summary>
    internal static readonly TimeSpan TransportGatePollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Gets whether Steam has progressed beyond process creation to a real
    /// Big Picture window. A cold-start SharedJSContext can accept evaluations before
    /// this point; early mutation was the distinguishing state in a device-observed
    /// startup failure. BOTH conditions are required — a live steam.exe alone is not
    /// a constructed Big Picture session.</summary>
    internal static bool IsReady => Steam.IsRunning && Steam.IsBigPictureVisible;

    /// <summary>Decides whether the Steam UI transport may carry any traffic at all.</summary>
    /// <param name="cefMasterEnabled">Whether Steam CEF integration is switched on.</param>
    /// <param name="inGameMode">Whether WSGM owns game mode, where it also owns Steam's start.</param>
    /// <param name="gameModeTransitionPending">Whether a transition is about to ask (or has just
    /// asked) Steam for Big Picture and has not settled yet. The request rebuilds Steam's whole
    /// front-end, so the hold must begin BEFORE it fires — waiting for the mode flag flips the
    /// gate seconds after Steam already started bootstrapping against injected state.</param>
    /// <param name="bigPictureReady">Whether <see cref="IsReady"/> held when the caller sampled it.</param>
    /// <returns>True to open the transport; false to hold every automatic CEF touch.</returns>
    /// <remarks>Desktop mode opens on the master switch alone: Steam there is the user's own
    /// windowed client, not a session WSGM is constructing, and the startup hang has only ever been
    /// observed while Steam constructs a Big Picture session.</remarks>
    internal static bool TransportShouldBeOpen(
        bool cefMasterEnabled, bool inGameMode, bool gameModeTransitionPending, bool bigPictureReady)
        => cefMasterEnabled && ((!inGameMode && !gameModeTransitionPending) || bigPictureReady);

    /// <summary>Runs one bounded automatic CEF operation after Big Picture and its target are ready.</summary>
    /// <param name="operation">Stable diagnostic name.</param>
    /// <param name="attemptAsync">Returns true when the operation completed, false to retry.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Whether the operation completed within the bounded retry window.</returns>
    internal static async Task<bool> RunWhenReadyAsync(
        string operation,
        Func<CancellationToken, Task<bool>> attemptAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(attemptAsync);
        bool waitingForBigPicture = false;
        for (int attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(
                    attempt == 0 ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(5),
                    cancellationToken).ConfigureAwait(false);
                if (!IsReady)
                {
                    if (!waitingForBigPicture)
                    {
                        waitingForBigPicture = true;
                        Log.Info($"{operation}: waiting for the Big Picture window.");
                    }
                    continue;
                }
                if (waitingForBigPicture)
                {
                    waitingForBigPicture = false;
                    Log.Info($"{operation}: Big Picture is ready; probing CEF.");
                }
                if (await attemptAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"{operation} attempt failed: {ex.Message}");
            }
        }

        Log.Info($"{operation}: Steam UI not reachable in time; deferring until the next trigger.");
        return false;
    }
}
