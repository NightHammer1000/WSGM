namespace WSGM.Core;

/// <summary>Routes the Steam UI machinery's diagnostics into WSGM's own log.</summary>
/// <remarks>
/// The one adapter between the two. It exists so the machinery can be lifted out of this
/// application without carrying <see cref="Log"/> with it, and so that its lines land in
/// <c>wsgm.log</c> exactly as they always have — which matters because remote diagnosis of the CEF
/// surface is a pasted copy of that file.
/// </remarks>
public sealed class WsgmSteamUiLog : ISteamUiLog
{
    /// <summary>Installs this adapter as the machinery's sink.</summary>
    public static void Install() => SteamUiLog.Use(new WsgmSteamUiLog());

    /// <inheritdoc />
    public void Info(string message) => Log.Info(message);

    /// <inheritdoc />
    public void Warn(string message) => Log.Warn(message);

    /// <inheritdoc />
    public void Change(string key, string message, bool warning = false) =>
        Log.Change(key, message, warning ? "warn " : "info ");
}
