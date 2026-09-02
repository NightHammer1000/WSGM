using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Controls;

internal enum PhysicalGlyphSurface
{
    DeviceDescription,
    NavigationHint,
}

internal enum PhysicalGlyphTheme
{
    Light,
    Dark,
    HighContrast,
}

internal sealed record PhysicalGlyphPath(
    Geometry Geometry,
    string Fill,
    string Stroke,
    decimal StrokeWidth,
    string FillRule,
    string StrokeLineCap,
    string StrokeLineJoin);

internal sealed record PhysicalGlyphRenderPlan
{
    internal required string? ProfileId { get; init; }
    internal required GlyphControlId RequestedControl { get; init; }
    internal required GlyphControlId? PhysicalControl { get; init; }
    internal required PhysicalGlyphFallbackReason FallbackReason { get; init; }
    internal required GlyphViewBox? ViewBox { get; init; }
    internal required IReadOnlyList<PhysicalGlyphPath> Paths { get; init; }
    internal required ReadOnlyMemory<byte> RasterPng { get; init; }

    internal bool UsesDeviceArtwork => Paths.Count > 0 || !RasterPng.IsEmpty;
}

/// <summary>
/// Bounded, path-free adapter from an imported physical profile to Avalonia-safe geometry plans.
/// </summary>
/// <remarks>
/// The service never opens a package file, parses SVG, or performs network work; it consumes only
/// the normalized model returned by the SDK's bounded package loader.
/// </remarks>
internal sealed class PhysicalGlyphService : IDisposable
{
    internal const int DefaultMaximumCacheEntries = 128;
    internal const int DefaultMaximumCacheBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly int _maximumCacheEntries;
    private readonly int _maximumCacheBytes;
    private readonly PhysicalGlyphCatalog _catalog;
    private readonly Dictionary<RenderCacheKey, CacheEntry> _cache = [];
    private readonly LinkedList<RenderCacheKey> _lru = [];
    private int _cacheBytes;

    internal PhysicalGlyphService(
        PhysicalGlyphCatalog catalog,
        int maximumCacheEntries = DefaultMaximumCacheEntries,
        int maximumCacheBytes = DefaultMaximumCacheBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (maximumCacheEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCacheEntries));
        }
        if (maximumCacheBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes));
        }

        _catalog = catalog;
        _maximumCacheEntries = maximumCacheEntries;
        _maximumCacheBytes = maximumCacheBytes;
        _catalog.Changed += ResetCache;
    }

    internal int CachedEntryCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    internal int CachedBytes
    {
        get
        {
            lock (_gate)
            {
                return _cacheBytes;
            }
        }
    }

    internal PhysicalGlyphRenderPlan Resolve(
        PhysicalGlyphSelectionResult selection,
        GlyphControlId requestedControl,
        PhysicalGlyphSurface surface,
        bool activeInputSourceIsManagedHandheld,
        PhysicalGlyphTheme theme,
        double scale)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Profile is null)
        {
            return FallbackPlan(requestedControl, selection.FallbackReason);
        }

        bool authorized = surface switch
        {
            PhysicalGlyphSurface.DeviceDescription => true,
            PhysicalGlyphSurface.NavigationHint => activeInputSourceIsManagedHandheld,
            _ => false,
        };
        if (!authorized)
        {
            return FallbackPlan(requestedControl, PhysicalGlyphFallbackReason.SourceNotHandheld);
        }

        int scaleBucket = Math.Clamp((int)Math.Round(scale * 4, MidpointRounding.AwayFromZero), 2, 16);
        RenderCacheKey key = new(
            selection.Profile.Manifest.ProfileId,
            selection.Profile.Manifest.Revision,
            requestedControl,
            theme,
            scaleBucket);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out CacheEntry? cached) && cached is not null)
            {
                Touch(cached);
                return cached.Plan;
            }

            PhysicalGlyphRenderPlan plan = BuildPlan(selection.Profile, requestedControl);
            int cost = EstimateCost(selection.Profile, plan);
            if (cost <= _maximumCacheBytes)
            {
                LinkedListNode<RenderCacheKey> node = _lru.AddFirst(key);
                _cache.Add(key, new CacheEntry(plan, cost, node));
                _cacheBytes += cost;
                TrimCache();
            }
            return plan;
        }
    }

    public void Dispose()
    {
        _catalog.Changed -= ResetCache;
        ResetCache();
    }

    private void ResetCache()
    {
        lock (_gate)
        {
            ClearCacheLocked();
        }
    }

    private static PhysicalGlyphRenderPlan BuildPlan(
        ImportedGlyphProfile profile,
        GlyphControlId requestedControl)
    {
        GlyphControlId physicalControl = requestedControl;
        GlyphControlAlias? alias = profile.Manifest.Aliases.FirstOrDefault(
            item => item.LogicalControl == requestedControl);
        if (alias is not null)
        {
            physicalControl = alias.PhysicalControl;
        }

        GlyphControlMapping? mapping = profile.Manifest.Controls.FirstOrDefault(
            item => item.Control == physicalControl);
        if (mapping is null || mapping.Presence is GlyphControlPresence.Absent)
        {
            return FallbackPlan(
                requestedControl,
                mapping is null
                    ? PhysicalGlyphFallbackReason.ArtworkMissing
                    : PhysicalGlyphFallbackReason.ControlAbsent,
                profile.Manifest.ProfileId,
                physicalControl);
        }

        if (mapping.AssetSha256 is not { } hash
            || !profile.Assets.TryGetValue(hash, out ImportedGlyphAsset? asset)
            || asset is null)
        {
            return FallbackPlan(
                requestedControl,
                PhysicalGlyphFallbackReason.ArtworkMissing,
                profile.Manifest.ProfileId,
                physicalControl);
        }

        if (asset.Vector is { } vector)
        {
            try
            {
                PhysicalGlyphPath[] paths = vector.Paths.Select(path => new PhysicalGlyphPath(
                    StreamGeometry.Parse(ToAvaloniaPathData(path.Data)),
                    path.Fill,
                    path.Stroke,
                    path.StrokeWidth,
                    path.FillRule,
                    path.StrokeLineCap,
                    path.StrokeLineJoin)).ToArray();
                return new PhysicalGlyphRenderPlan
                {
                    ProfileId = profile.Manifest.ProfileId,
                    RequestedControl = requestedControl,
                    PhysicalControl = physicalControl,
                    FallbackReason = PhysicalGlyphFallbackReason.None,
                    ViewBox = vector.ViewBox,
                    Paths = paths,
                    RasterPng = default,
                };
            }
            catch (Exception)
            {
                // The importer accepted only its strict path grammar, but Avalonia is the final
                // renderer authority. A parser-version disagreement is a bounded fallback, never a
                // reason to expose source SVG bytes or take down the overlay.
                return FallbackPlan(
                    requestedControl,
                    PhysicalGlyphFallbackReason.RenderRejected,
                    profile.Manifest.ProfileId,
                    physicalControl);
            }
        }

        return new PhysicalGlyphRenderPlan
        {
            ProfileId = profile.Manifest.ProfileId,
            RequestedControl = requestedControl,
            PhysicalControl = physicalControl,
            FallbackReason = PhysicalGlyphFallbackReason.None,
            ViewBox = null,
            Paths = [],
            RasterPng = asset.RasterPng,
        };
    }

    private static int EstimateCost(
        ImportedGlyphProfile profile,
        PhysicalGlyphRenderPlan plan)
    {
        if (plan.PhysicalControl is not { } control)
        {
            return 64;
        }
        GlyphControlMapping? mapping = profile.Manifest.Controls.FirstOrDefault(
            item => item.Control == control);
        return mapping?.AssetSha256 is { } hash
            && profile.Assets.TryGetValue(hash, out ImportedGlyphAsset? asset)
            && asset is not null
            ? Math.Max(64, asset.RetainedBytes)
            : 64;
    }

    private static string ToAvaloniaPathData(string normalized)
    {
        string[] tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        StringBuilder output = new(normalized.Length + 16);
        int index = 0;
        while (index < tokens.Length)
        {
            string command = tokens[index++];
            if (output.Length > 0)
            {
                output.Append(' ');
            }
            output.Append(command);
            int arity = char.ToUpperInvariant(command[0]) switch
            {
                'M' or 'L' or 'T' => 2,
                'H' or 'V' => 1,
                'C' => 6,
                'S' or 'Q' => 4,
                'A' => 7,
                'Z' => 0,
                _ => throw new FormatException("Imported glyph path has an unsupported command."),
            };
            while (arity > 0 && index < tokens.Length && !char.IsAsciiLetter(tokens[index][0]))
            {
                if (index + arity > tokens.Length)
                {
                    throw new FormatException("Imported glyph path has an incomplete command.");
                }

                output.Append(' ');
                if (arity == 1)
                {
                    output.Append(tokens[index]);
                }
                else if (arity == 7)
                {
                    output.Append(tokens[index]).Append(',').Append(tokens[index + 1])
                        .Append(' ').Append(tokens[index + 2])
                        .Append(' ').Append(tokens[index + 3])
                        .Append(' ').Append(tokens[index + 4])
                        .Append(' ').Append(tokens[index + 5]).Append(',').Append(tokens[index + 6]);
                }
                else
                {
                    for (int parameter = 0; parameter < arity; parameter += 2)
                    {
                        if (parameter > 0)
                        {
                            output.Append(' ');
                        }
                        output.Append(tokens[index + parameter]).Append(',')
                            .Append(tokens[index + parameter + 1]);
                    }
                }
                index += arity;
            }
        }
        return output.ToString();
    }

    private static PhysicalGlyphRenderPlan FallbackPlan(
        GlyphControlId requestedControl,
        PhysicalGlyphFallbackReason reason,
        string? profileId = null,
        GlyphControlId? physicalControl = null) => new()
        {
            ProfileId = profileId,
            RequestedControl = requestedControl,
            PhysicalControl = physicalControl,
            FallbackReason = reason,
            ViewBox = null,
            Paths = [],
            RasterPng = default,
        };

    private void Touch(CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private void TrimCache()
    {
        while (_cache.Count > _maximumCacheEntries || _cacheBytes > _maximumCacheBytes)
        {
            LinkedListNode<RenderCacheKey>? tail = _lru.Last;
            if (tail is null
                || !_cache.Remove(tail.Value, out CacheEntry? removed)
                || removed is null)
            {
                break;
            }
            _lru.Remove(tail);
            _cacheBytes -= removed.Cost;
        }
    }

    private void ClearCacheLocked()
    {
        _cache.Clear();
        _lru.Clear();
        _cacheBytes = 0;
    }

    private readonly record struct RenderCacheKey(
        string ProfileId,
        int Revision,
        GlyphControlId Control,
        PhysicalGlyphTheme Theme,
        int ScaleBucket);

    private sealed record CacheEntry(
        PhysicalGlyphRenderPlan Plan,
        int Cost,
        LinkedListNode<RenderCacheKey> Node);
}
