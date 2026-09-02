using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Result of a live library add through Steam's own client.</summary>
public enum SteamLibraryAddStatus
{
    /// <summary>Steam adopted the folder as a new library.</summary>
    Added,
    /// <summary>The drive already carries a Steam library (nothing to do).</summary>
    AlreadyPresent,
    /// <summary>Steam actively refused the folder; <c>Detail</c> is its reason.</summary>
    Rejected,
    /// <summary>The debug channel could not be reached, so no live add happened.</summary>
    Unavailable,
}

/// <summary>Outcome of a live library add.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryAddResult(SteamLibraryAddStatus Status, string? Detail);

/// <summary>Result of removing a live Steam library selected by its content id.</summary>
public enum SteamLibraryRemoveStatus
{
    /// <summary>Steam removed the library and will persist the change.</summary>
    Removed,
    /// <summary>The content id is not registered or its folder is already absent.</summary>
    NotPresent,
    /// <summary>Steam actively refused the removal; <c>Detail</c> is its reason.</summary>
    Rejected,
    /// <summary>The debug channel could not be reached, so no live removal happened.</summary>
    Unavailable,
}

/// <summary>Outcome of removing a live library.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryRemoveResult(
    SteamLibraryRemoveStatus Status, string? Detail);

/// <summary>Result of relabeling a live Steam library selected by its content id.</summary>
public enum SteamLibraryLabelStatus
{
    /// <summary>Steam applied the label and will persist it.</summary>
    Applied,
    /// <summary>The content id is not registered or its folder is not live.</summary>
    NotPresent,
    /// <summary>Steam actively refused; <c>Detail</c> is its reason.</summary>
    Rejected,
    /// <summary>The debug channel could not be reached, so nothing changed.</summary>
    Unavailable,
}

/// <summary>Outcome of relabeling a live library.</summary>
/// <param name="Status">What Steam did.</param>
/// <param name="Detail">Steam's reason code, when it gave one.</param>
public readonly record struct SteamLibraryLabelResult(
    SteamLibraryLabelStatus Status, string? Detail);

/// <summary>Adds a Steam library to the RUNNING client by driving Steam's own
/// front-end API over its CEF remote-debugging port (see <see cref="SteamCef"/>):
/// a <c>Runtime.evaluate</c> calls <c>SteamClient.InstallFolder.AddInstallFolder</c>,
/// so Steam adopts, persists, mounts and scans the folder on its own thread with no
/// restart. This is version-proof (no binary offsets) and safe (Steam performs the
/// operation), unlike poking the client's internals in-process.</summary>
public static class SteamCdp
{
    /// <summary>Writes the CEF remote-debugging flag so Steam opens its localhost
    /// devtools port on next start. Idempotent and best-effort.</summary>
    /// <remarks>Finding Steam is WSGM's job, not the toolkit's, so the directory is passed in.</remarks>
    public static bool EnsureRemoteDebuggingEnabled() =>
        SteamCef.EnsureRemoteDebuggingEnabled(Steam.InstallDirectory);

    /// <summary>Blocking wrapper for worker-thread callers (never call on the UI thread).</summary>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="label">A label to apply after adding, or null/empty for none.</param>
    /// <param name="replaceExisting">True when the caller has just CREATED the
    /// library at this path, which makes every prior registration there stale by
    /// definition. See <see cref="AddLibraryAsync"/>.</param>
    public static SteamLibraryAddResult AddLibrary(
        string libraryPath, string? label = null, bool replaceExisting = false)
        => AddLibraryAsync(libraryPath, label, replaceExisting).GetAwaiter().GetResult();

    /// <summary>Adds <paramref name="libraryPath"/> to the live Steam client and,
    /// on success, labels it.</summary>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="label">A label to apply after adding, or null/empty for none.</param>
    /// <param name="replaceExisting">
    /// True when the caller has just CREATED a brand-new library at this path — a
    /// card format. Every registration already at the path then belongs to a card
    /// that is no longer there, including one Steam still reports as mounted (it
    /// keeps that flag while the volume is present but reports zero capacity, so
    /// "mounted" alone does not prove a registration is current). All of them are
    /// removed before the add. False for an ordinary add, which keeps a mounted
    /// registration and adopts it rather than churning a live library.
    /// </param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<SteamLibraryAddResult> AddLibraryAsync(
        string libraryPath, string? label = null, bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        var result = await SteamUiTransportSession.EvaluateAsync(
            BuildAddExpression(libraryPath, label, replaceExisting),
            TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, result.Error);
        }
        return Interpret(result.Value);
    }

    /// <summary>Removes the live Steam library whose registration carries
    /// <paramref name="contentId"/>. Steam's folder API does not expose content
    /// ids, so the id first selects its registered path; only that path's current
    /// live folder index is passed to Steam. This makes a reused card-reader drive
    /// letter unable to select a different card's library.</summary>
    /// <param name="contentId">The stable identity read from the card marker.</param>
    /// <param name="libraryFoldersVdf">Steam's current libraryfolders configuration.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The live removal outcome.</returns>
    public static async Task<SteamLibraryRemoveResult> RemoveLibraryByContentIdAsync(
        string contentId, string libraryFoldersVdf, CancellationToken cancellationToken = default)
    {
        var libraryPath = Shell.SteamLibraryVdf.PathForContentId(libraryFoldersVdf, contentId);
        if (libraryPath is null)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.NotPresent, null);
        }
        // The same normalizer the injected script uses (docs\steam-cef.md §8): the two forms have
        // to agree, and a bare trim does not — it leaves "D:/Games" and "D:\Games" unequal here
        // while Steam's side treats them as one folder.
        var normalized = Shell.SteamLibraryVdf.NormalizePath(libraryPath);
        var matchingPaths = Shell.SteamLibraryVdf.ValuesOf(libraryFoldersVdf, "path")
            .Count(path => string.Equals(
                Shell.SteamLibraryVdf.NormalizePath(path),
                normalized,
                StringComparison.Ordinal));
        if (matchingPaths != 1)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Rejected,
                "ContentIdPathAmbiguous");
        }
        var result = await SteamUiTransportSession.EvaluateAsync(
            BuildRemoveExpression(libraryPath), TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Unavailable, result.Error);
        }
        return InterpretRemove(result.Value);
    }

    /// <summary>Removes EVERY live registration at <paramref name="libraryPath"/>,
    /// whichever card each one belonged to.</summary>
    /// <remarks>
    /// The identity-free counterpart of <see cref="RemoveLibraryByContentIdAsync"/>,
    /// for the case where identity is exactly what is missing: the card that owned the
    /// registration has left the reader, so its marker cannot be read and Steam's
    /// folder API never exposed the content id in the first place. Selecting by path
    /// is safe here precisely because the caller has established that nothing at that
    /// path is current — see <see cref="Shell.CardVolumeMonitor"/>.
    /// </remarks>
    /// <param name="libraryPath">The library folder, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The live removal outcome.</returns>
    public static async Task<SteamLibraryRemoveResult> RemoveLibrariesAtPathAsync(
        string libraryPath, CancellationToken cancellationToken = default)
    {
        var result = await SteamUiTransportSession.EvaluateAsync(
            BuildRemoveExpression(libraryPath), TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Unavailable, result.Error);
        }
        return InterpretRemove(result.Value);
    }

    /// <summary>Relabels the live Steam library whose registration carries
    /// <paramref name="contentId"/> (same identity discipline as removal: the id
    /// selects its registered path, and an ambiguous path — several registrations
    /// at one reused reader letter — refuses rather than guessing). Uses the same
    /// <c>SetFolderLabel</c> call the add flow already applies after a live add.</summary>
    /// <param name="contentId">The stable identity read from the card marker.</param>
    /// <param name="libraryFoldersVdf">Steam's current libraryfolders configuration.</param>
    /// <param name="label">The new label.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<SteamLibraryLabelResult> SetLibraryLabelByContentIdAsync(
        string contentId, string libraryFoldersVdf, string label,
        CancellationToken cancellationToken = default)
    {
        var libraryPath = Shell.SteamLibraryVdf.PathForContentId(libraryFoldersVdf, contentId);
        if (libraryPath is null)
        {
            return new SteamLibraryLabelResult(SteamLibraryLabelStatus.NotPresent, null);
        }
        // Same normalizer as the injected script — see the remove path above.
        var normalized = Shell.SteamLibraryVdf.NormalizePath(libraryPath);
        var matchingPaths = Shell.SteamLibraryVdf.ValuesOf(libraryFoldersVdf, "path")
            .Count(path => string.Equals(
                Shell.SteamLibraryVdf.NormalizePath(path),
                normalized,
                StringComparison.Ordinal));
        if (matchingPaths != 1)
        {
            return new SteamLibraryLabelResult(SteamLibraryLabelStatus.Rejected,
                "ContentIdPathAmbiguous");
        }
        var result = await SteamUiTransportSession.EvaluateAsync(
            BuildLabelExpression(libraryPath, label), TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new SteamLibraryLabelResult(SteamLibraryLabelStatus.Unavailable, result.Error);
        }
        return InterpretLabel(result.Value);
    }

    /// <summary>Normalizes a Steam folder path for comparison: trailing separators
    /// dropped, case folded. Shared by every expression here so the add, remove and
    /// relabel paths can never disagree about what "the same folder" means. A
    /// mismatch here would silently skip the stale-registration purge and put the
    /// duplicate-library bug straight back, so separator DIRECTION is unified too
    /// and not just trailing separators trimmed.</summary>
    private const string NormalizePathJs =
        "const norm=p=>String(p||'').replace(/\\//g,'\\\\')"
        + ".replace(/\\\\+$/,'').toLowerCase();";

    /// <summary>Builds the JS that adds the folder, labels it when a label is
    /// given, and reports the outcome as a JSON string. Both the path and label are
    /// JSON-encoded into JS string literals — a raw path would lose its backslashes
    /// and Steam would reject the malformed path.</summary>
    /// <remarks>
    /// <para>
    /// <b>Stale same-path registrations are purged first, and that is the whole
    /// point of this expression</b> (live-verified against a running client,
    /// 2026-08-20). Steam keys an install folder by its PATH and never dedupes the
    /// list. A card pulled out of the reader leaves its registration behind,
    /// unmounted, still carrying the app list and capacity it had when it was last
    /// seen. Calling <c>AddInstallFolder</c> for that same path does NOT adopt or
    /// replace it — Steam APPENDS a second entry, and the client is left holding
    /// two folders at one path: the phantom with the previous card's games, the new
    /// one with the real capacity. That is exactly the reported "the new card shows
    /// the previous card's games but the right size", and it survives ejecting the
    /// card because the phantom was never tied to the card at all. Only a Steam
    /// restart clears it, because the next start rebuilds the list from disk.
    /// </para>
    /// <para>
    /// The purge deliberately spares a MOUNTED registration at the same path and
    /// adopts it instead of re-adding: Steam refuses a second add there anyway (it
    /// answers <c>NotWritableFolder</c>, which reads as a hard failure), and
    /// removing a live library only to add it back would drop the user's games out
    /// of the UI for the length of a rescan.
    /// </para>
    /// </remarks>
    private static string BuildAddExpression(
        string libraryPath, string? label, bool replaceExisting)
    {
        var pathLiteral = SteamCef.JsString(libraryPath);
        var labelLiteral = string.IsNullOrEmpty(label) ? "null" : SteamCef.JsString(label);
        return "(async()=>{try{const path=" + pathLiteral + ";const l=" + labelLiteral + ";"
            + NormalizePathJs
            + "const target=norm(path);let purged=0;"
            + "const folders=await SteamClient.InstallFolder.GetInstallFolders();"
            + "const same=folders.filter(x=>norm(x.strFolderPath)===target);"
            // A just-formatted card makes every prior registration at this path
            // stale, mounted or not: Steam keeps the mounted flag while the volume
            // is present and only zeroes the capacity, so it cannot be trusted to
            // mean "this is still the card that library belongs to".
            + (replaceExisting ? "const live=null;" : "const live=same.find(x=>x.bIsMounted);")
            // Everything at this path Steam has not mounted is a leftover, and so
            // is any surplus mounted duplicate beyond the one being kept.
            // nFolderIndex is a STABLE ID, not an array position (live-measured
            // 2026-08-23, invariant 8): removing one entry does not renumber the
            // others, so removing several in one pass off a single GetInstallFolders
            // snapshot is correct as written. No descending sort, and no re-fetch
            // between removals.
            + "for(const f of same){if(f===live)continue;"
            + "try{await SteamClient.InstallFolder.RemoveInstallFolder(f.nFolderIndex);purged++;}catch(e){}}"
            + "const i=live?live.nFolderIndex:await SteamClient.InstallFolder.AddInstallFolder(path);"
            + "if(l!==null&&typeof i==='number'&&i>=0){"
            + "try{await SteamClient.InstallFolder.SetFolderLabel(i,l);}catch(e){}}"
            + "return JSON.stringify({ok:true,index:i,purged:purged,existing:!!live});}"
            + "catch(e){return JSON.stringify({ok:false,result:(e&&e.result),message:(e&&e.message)});}})()";
    }

    private static string BuildLabelExpression(string libraryPath, string label)
    {
        var pathLiteral = SteamCef.JsString(libraryPath);
        var labelLiteral = SteamCef.JsString(label);
        return "(async()=>{try{const path=" + pathLiteral + ";" + NormalizePathJs
            + "const folders=await SteamClient.InstallFolder.GetInstallFolders();"
            + "const same=folders.filter(x=>norm(x.strFolderPath)===norm(path));"
            // A mounted registration is the one the user is looking at. A phantom
            // left by a previous card can sit at the same path and comes FIRST in
            // Steam's list, so taking the first match would label the wrong one.
            + "const folder=same.find(x=>x.bIsMounted)||same[0];"
            + "if(!folder)return JSON.stringify({ok:true,absent:true});"
            + "await SteamClient.InstallFolder.SetFolderLabel(folder.nFolderIndex," + labelLiteral + ");"
            + "return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false,result:(e&&e.result),message:(e&&e.message)});}})()";
    }

    private static string BuildRemoveExpression(string libraryPath)
    {
        var pathLiteral = SteamCef.JsString(libraryPath);
        return "(async()=>{try{const path=" + pathLiteral + ";" + NormalizePathJs
            + "const folders=await SteamClient.InstallFolder.GetInstallFolders();"
            // EVERY registration at the path, not just the first: Steam allows
            // duplicates at one path, so removing a single match can leave the
            // phantom behind and make the removal look like it did nothing.
            + "const same=folders.filter(x=>norm(x.strFolderPath)===norm(path));"
            + "if(!same.length)return JSON.stringify({ok:true,absent:true});"
            + "let removed=0;"
            // Same reason as the purge in BuildAddExpression: nFolderIndex is a
            // stable id, not a position. Steam's own store looks folders up with
            // find(f=>f.nFolderIndex==e) and exposes array position separately, and
            // removing index 2 of [0,1,2,3] leaves 0,1,3 (live-measured 2026-08-23,
            // invariant 8). Iterating one snapshot in order is therefore correct.
            + "for(const f of same){await SteamClient.InstallFolder.RemoveInstallFolder(f.nFolderIndex);removed++;}"
            + "return JSON.stringify({ok:true,removed:removed});}catch(e){return JSON.stringify({ok:false,result:(e&&e.result),message:(e&&e.message)});}})()";
    }

    /// <summary>Maps Steam's JSON reply to a result. Success carries no reason;
    /// <c>DriveAlreadyHasLibrary</c> is treated as already-present; anything else is
    /// a genuine rejection with Steam's own reason code.</summary>
    private static SteamLibraryAddResult Interpret(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryAddResult(
                SteamLibraryAddStatus.Unavailable, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                if (root.TryGetProperty("purged", out var purgedCount)
                    && purgedCount.TryGetInt32(out var purged) && purged > 0)
                {
                    // Device-diagnosable on its own: this line is what identifies a
                    // reader handing the same drive letter to a different card.
                    Log.Info($"Steam library add: purged {purged} stale registration(s) "
                        + "at the same path first.");
                }
                if (root.TryGetProperty("existing", out var existing)
                    && existing.ValueKind == JsonValueKind.True)
                {
                    Log.Info("Steam library already mounted at this path; adopted it.");
                    return new SteamLibraryAddResult(
                        SteamLibraryAddStatus.AlreadyPresent, "AlreadyMounted");
                }
                Log.Info("Steam library added to the live client.");
                return new SteamLibraryAddResult(SteamLibraryAddStatus.Added, null);
            }

            var message = root.TryGetProperty("message", out var reason)
                && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
            message ??= root.TryGetProperty("result", out var resultCode)
                ? $"EResult {resultCode.GetRawText()}" : null;

            if (string.Equals(message, "DriveAlreadyHasLibrary", StringComparison.Ordinal))
            {
                return new SteamLibraryAddResult(SteamLibraryAddStatus.AlreadyPresent, message);
            }
            Log.Warn($"Steam rejected the library add: {message ?? "unknown reason"}.");
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Rejected, message);
        }
        catch (Exception ex)
        {
            return new SteamLibraryAddResult(SteamLibraryAddStatus.Unavailable, ex.Message);
        }
    }

    private static SteamLibraryLabelResult InterpretLabel(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryLabelResult(
                SteamLibraryLabelStatus.Unavailable, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var status = root.TryGetProperty("absent", out var absent)
                    && absent.ValueKind == JsonValueKind.True
                    ? SteamLibraryLabelStatus.NotPresent : SteamLibraryLabelStatus.Applied;
                return new SteamLibraryLabelResult(status, null);
            }
            var message = root.TryGetProperty("message", out var reason)
                && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() : null;
            message ??= root.TryGetProperty("result", out var resultCode)
                ? $"EResult {resultCode.GetRawText()}" : null;
            Log.Warn($"Steam rejected the library relabel: {message ?? "unknown reason"}.");
            return new SteamLibraryLabelResult(SteamLibraryLabelStatus.Rejected, message);
        }
        catch (Exception ex)
        {
            return new SteamLibraryLabelResult(SteamLibraryLabelStatus.Unavailable, ex.Message);
        }
    }

    private static SteamLibraryRemoveResult InterpretRemove(string? jsonValue)
    {
        if (jsonValue is null)
        {
            return new SteamLibraryRemoveResult(
                SteamLibraryRemoveStatus.Unavailable, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(jsonValue);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var status = root.TryGetProperty("absent", out var absent)
                    && absent.ValueKind == JsonValueKind.True
                    ? SteamLibraryRemoveStatus.NotPresent : SteamLibraryRemoveStatus.Removed;
                return new SteamLibraryRemoveResult(status, null);
            }
            var message = root.TryGetProperty("message", out var reason)
                && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() : null;
            message ??= root.TryGetProperty("result", out var resultCode)
                ? $"EResult {resultCode.GetRawText()}" : null;
            Log.Warn($"Steam rejected the library removal: {message ?? "unknown reason"}.");
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Rejected, message);
        }
        catch (Exception ex)
        {
            return new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Unavailable, ex.Message);
        }
    }
}
