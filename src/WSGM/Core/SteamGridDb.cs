using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>An artwork slot. The numeric values are Steam's own <c>eAssetType</c>
/// (capsule/portrait = 0, hero = 1, logo = 2, wide capsule = 3, icon = 4) so they pass
/// straight into <see cref="SteamArtwork"/>'s <c>SetCustomArtworkForApp</c> call.</summary>
public enum ArtworkAsset
{
    /// <summary>Portrait capsule (600×900).</summary>
    Grid = 0,

    /// <summary>Hero banner (1920×620).</summary>
    Hero = 1,

    /// <summary>Transparent logo.</summary>
    Logo = 2,

    /// <summary>Wide capsule (460×215).</summary>
    Wide = 3,

    /// <summary>Icon.</summary>
    Icon = 4,
}

/// <summary>One artwork candidate from SteamGridDB.</summary>
/// <param name="Id">SteamGridDB asset id.</param>
/// <param name="Url">Full-resolution image URL.</param>
/// <param name="Thumb">Thumbnail URL (for the picker grid).</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Extension">Verified static image format, <c>png</c> or <c>jpg</c>.</param>
public sealed record SgdbAsset(int Id, string Url, string Thumb, int Width, int Height, string Extension);

/// <summary>A SteamGridDB request failed for a reason the UI should surface.</summary>
public sealed class SteamGridDbException : Exception
{
    /// <summary>Creates a request failure with a user-facing message.</summary>
    public SteamGridDbException(string message) : base(message) { }
}

/// <summary>A game match from a SteamGridDB title search.</summary>
/// <param name="Id">SteamGridDB game id.</param>
/// <param name="Name">Game name.</param>
public sealed record SgdbGame(int Id, string Name);

/// <summary>Read-only client for the SteamGridDB v2 REST API: title search and per-slot
/// asset listing, plus raw image download. Uses only <see cref="HttpClient"/> and <see cref="JsonDocument"/>.
/// Auth is a bearer key the user sets in Settings (<see cref="ResolveKey"/>); there is no
/// bundled key (SteamGridDB rejects the decky public key). Applying the chosen image
/// is <see cref="SteamArtwork"/>'s job; this class only fetches.</summary>
public static class SteamGridDb
{
    private const string ApiBase = "https://www.steamgriddb.com/api/v2";

    /// <summary>Where a user gets a free SteamGridDB API key (shown in Settings).</summary>
    public const string KeyPageUrl = "https://www.steamgriddb.com/profile/preferences/api";

    // MaxResponseContentBufferSize bounds the BUFFERED reads — the JSON endpoints, whose
    // bodies are a few hundred KB at most — so a hostile or malfunctioning response
    // cannot buffer without limit into a string on a memory-constrained handheld. It
    // does not apply to the image download, which streams with ResponseHeadersRead and
    // enforces its own 16 MB counted cap.
    private const int MaxJsonResponseBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = MaxJsonResponseBytes,
    };

    /// <summary>The user's configured API key (trimmed), or empty. There is no bundled
    /// key — SteamGridDB rejects the decky public key — so the user must set their own
    /// free key in Settings (see <see cref="KeyPageUrl"/>).</summary>
    /// <param name="config">The loaded configuration.</param>
    public static string ResolveKey(AppConfig config)
        => (config.SteamGridDbApiKey ?? "").Trim();

    /// <summary>Searches SteamGridDB for games by title (autocomplete).</summary>
    /// <param name="term">The search term.</param>
    /// <param name="key">The bearer API key (see <see cref="ResolveKey"/>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<IReadOnlyList<SgdbGame>> SearchGamesAsync(
        string term, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return Array.Empty<SgdbGame>();
        }
        var url = $"{ApiBase}/search/autocomplete/{Uri.EscapeDataString(term.Trim())}";
        var root = await GetAsync(url, key, cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SgdbGame>();
        }
        var list = new List<SgdbGame>();
        foreach (var game in data.EnumerateArray())
        {
            if (game.TryGetProperty("id", out var id) && id.TryGetInt32(out var gameId))
            {
                list.Add(new SgdbGame(gameId, game.TryGetProperty("name", out var n)
                    ? n.GetString() ?? "" : ""));
            }
        }
        return list;
    }

    /// <summary>Lists artwork candidates for a Steam app id in the given slot. Grid vs
    /// Wide are the same SteamGridDB endpoint filtered by dimensions.</summary>
    /// <param name="asset">Which artwork slot.</param>
    /// <param name="steamAppId">The Steam app id.</param>
    /// <param name="key">The bearer API key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<IReadOnlyList<SgdbAsset>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, string key, CancellationToken cancellationToken = default)
        => GetAssetsAsync(asset, "steam", steamAppId.ToString(CultureInfo.InvariantCulture), key,
            cancellationToken);

    /// <summary>Lists artwork candidates for a SteamGridDB game id (used when a Steam
    /// app has no direct SteamGridDB mapping and the user searched by title).</summary>
    /// <param name="asset">Which artwork slot.</param>
    /// <param name="sgdbGameId">The SteamGridDB game id.</param>
    /// <param name="key">The bearer API key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<IReadOnlyList<SgdbAsset>> GetAssetsForGameAsync(
        ArtworkAsset asset, int sgdbGameId, string key, CancellationToken cancellationToken = default)
        => GetAssetsAsync(asset, "game", sgdbGameId.ToString(CultureInfo.InvariantCulture), key,
            cancellationToken);

    private static async Task<IReadOnlyList<SgdbAsset>> GetAssetsAsync(
        ArtworkAsset asset, string idKind, string id, string key, CancellationToken cancellationToken)
    {
        var (segment, dimensions) = asset switch
        {
            ArtworkAsset.Grid => ("grids", "600x900"),
            ArtworkAsset.Wide => ("grids", "460x215"),
            ArtworkAsset.Hero => ("heroes", null),
            ArtworkAsset.Logo => ("logos", null),
            ArtworkAsset.Icon => ("icons", null),
            _ => ("grids", null),
        };
        var url = $"{ApiBase}/{segment}/{idKind}/{id}?types=static";
        if (dimensions is not null)
        {
            url += $"&dimensions={dimensions}";
        }

        var root = await GetAsync(url, key, cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SgdbAsset>();
        }
        var list = new List<SgdbAsset>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var full = urlEl.GetString() ?? "";
            var thumb = item.TryGetProperty("thumb", out var t) ? t.GetString() ?? full : full;
            var w = item.TryGetProperty("width", out var wi) && wi.TryGetInt32(out var wv) ? wv : 0;
            var h = item.TryGetProperty("height", out var he) && he.TryGetInt32(out var hv) ? hv : 0;
            var assetId = item.TryGetProperty("id", out var ai) && ai.TryGetInt32(out var av) ? av : 0;
            var extension = ImageExtension(full);
            if (full.Length > 0 && extension is not null)
            {
                list.Add(new SgdbAsset(assetId, full, thumb, w, h, extension));
            }
        }
        return list;
    }

    /// <summary>Downloads raw image bytes from a URL (SteamGridDB CDN or Steam's own
    /// store CDN), capped at 16 MB. There is no null failure result: every failure —
    /// a non-HTTPS URL, an HTTP error, an oversized body, a transport fault — throws
    /// <see cref="SteamGridDbException"/> carrying a user-facing message, so callers
    /// must wrap the call. The nullable return type is defensive only.</summary>
    /// <param name="url">The image URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<byte[]?> DownloadImageAsync(
        string url, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new SteamGridDbException("Artwork URL was not a secure HTTPS address.");
            }
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            const int maxBytes = 16 * 1024 * 1024;
            if (response.Content.Headers.ContentLength is > maxBytes)
            {
                throw new SteamGridDbException("Artwork is larger than the 16 MB safety limit.");
            }
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (output.Length + read > maxBytes)
                {
                    throw new SteamGridDbException("Artwork is larger than the 16 MB safety limit.");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (SteamGridDbException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"SteamGridDB image download failed ({url}): {ex.Message}");
            throw new SteamGridDbException("Could not download the artwork image.");
        }
    }

    private static async Task<JsonElement?> GetAsync(
        string url, string key, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"SteamGridDB {(int)response.StatusCode} for {url}.");
                throw new SteamGridDbException(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        => "SteamGridDB rejected the API key.",
                    System.Net.HttpStatusCode.TooManyRequests
                        => "SteamGridDB rate limit reached. Try again later.",
                    _ => $"SteamGridDB returned HTTP {(int)response.StatusCode}.",
                });
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            // Clone so the element survives disposal of the document.
            return document.RootElement.Clone();
        }
        catch (SteamGridDbException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"SteamGridDB request failed ({url}): {ex.Message}");
            throw new SteamGridDbException("Could not contact SteamGridDB.");
        }
    }

    private static string? ImageExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "jpg",
            ".png" => "png",
            _ => null,
        };
    }
}
