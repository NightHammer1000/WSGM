using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

internal enum PhysicalGlyphFallbackReason
{
    None,
    DeviceIntegrationDisabled,
    NativeSteamSelected,
    ExactDeviceMismatch,
    SourceNotHandheld,
    ControlAbsent,
    ArtworkMissing,
    RenderRejected,
}

internal sealed record PhysicalGlyphSelectionResult(
    ImportedGlyphProfile? Profile,
    PhysicalGlyphFallbackReason FallbackReason,
    bool FellBackFromMissingManualProfile);

/// <summary>Owns immutable package profiles and applies the closed physical-glyph selection policy.</summary>
internal sealed class PhysicalGlyphCatalog : IDisposable
{
    private readonly object _gate = new();
    private Dictionary<string, ImportedGlyphProfile> _profiles = new(StringComparer.Ordinal);
    private bool _disposed;

    private string? _activeDeviceId;

    internal event Action? Changed;

    /// <summary>Records which device definition the active plugin matched.</summary>
    /// <param name="deviceDefinitionId">The matched definition, or null when none is active.</param>
    /// <remarks>
    /// Held here rather than by the caller because selection depends on it exactly as it depends on
    /// the profiles, and both can arrive in either order: the definition comes from a lifecycle
    /// notification and the profiles from the installed package. Whichever lands second has to
    /// re-raise <see cref="Changed"/>, or every surface keeps the answer computed before the pair
    /// was complete.
    /// </remarks>
    internal void SetActiveDevice(string? deviceDefinitionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (string.Equals(_activeDeviceId, deviceDefinitionId, StringComparison.Ordinal))
            {
                return;
            }

            _activeDeviceId = deviceDefinitionId;
        }

        Changed?.Invoke();
    }

    internal void ReplacePackageProfiles(IEnumerable<ImportedGlyphProfile> profiles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profiles);
        ImportedGlyphProfile[] snapshot = profiles
            .OrderBy(profile => profile.Manifest.ProfileId, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, ImportedGlyphProfile> replacement = new(StringComparer.Ordinal);
        foreach (ImportedGlyphProfile profile in snapshot)
        {
            if (!replacement.TryAdd(profile.Manifest.ProfileId, profile))
            {
                throw new ArgumentException(
                    $"Profile '{profile.Manifest.ProfileId}' appears more than once.",
                    nameof(profiles));
            }
        }

        lock (_gate)
        {
            _profiles = replacement;
        }
        Changed?.Invoke();
    }

    internal PhysicalGlyphSelectionResult SelectProfile(
        bool deviceIntegrationEnabled,
        DeviceGlyphSelection selectionMode,
        string? manualProfileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            string? activeDeviceId = _activeDeviceId;

            // Every glyph surface funnels through here, so log the decisive inputs once whenever
            // the selection changes and make every fallback remotely diagnosable.
            Log.Change(
                "glyph.selection",
                $"Glyph selection: integration={deviceIntegrationEnabled}, mode={selectionMode}, "
                    + $"device={activeDeviceId ?? "<none>"}, profiles={_profiles.Count}, "
                    + $"manual={manualProfileId ?? "<none>"}");
            if (!deviceIntegrationEnabled)
            {
                return Fallback(PhysicalGlyphFallbackReason.DeviceIntegrationDisabled);
            }
            if (selectionMode is DeviceGlyphSelection.NativeSteam)
            {
                return Fallback(PhysicalGlyphFallbackReason.NativeSteamSelected);
            }

            bool missingManual = false;
            if (selectionMode is DeviceGlyphSelection.ManualReviewedProfile)
            {
                if (manualProfileId is { Length: > 0 }
                    && activeDeviceId is { Length: > 0 }
                    && _profiles.TryGetValue(manualProfileId, out ImportedGlyphProfile? manual)
                    && manual.Manifest.ExactDeviceIds.Contains(activeDeviceId, StringComparer.Ordinal))
                {
                    return new PhysicalGlyphSelectionResult(
                        manual,
                        PhysicalGlyphFallbackReason.None,
                        false);
                }

                // A missing manual profile falls back to Automatic and reports the missing
                // selection; it never guesses another manual profile.
                missingManual = true;
            }

            if (activeDeviceId is not { Length: > 0 })
            {
                return new PhysicalGlyphSelectionResult(
                    null,
                    PhysicalGlyphFallbackReason.ExactDeviceMismatch,
                    missingManual);
            }

            // Automatic selection is the package's own profile for the matched device. Naming the
            // device is the whole discriminator; a package wanting a different profile for the same
            // device uses the manual selection above.
            ImportedGlyphProfile? automatic = _profiles.Values
                .Where(profile =>
                    profile.Manifest.ExactDeviceIds.Contains(activeDeviceId, StringComparer.Ordinal))
                .OrderBy(profile => profile.Manifest.ProfileId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (automatic is null)
            {
                return new PhysicalGlyphSelectionResult(
                    null,
                    PhysicalGlyphFallbackReason.ExactDeviceMismatch,
                    missingManual);
            }

            return new PhysicalGlyphSelectionResult(
                automatic,
                PhysicalGlyphFallbackReason.None,
                missingManual);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _profiles.Clear();
        }
        Changed = null;
    }

    private static PhysicalGlyphSelectionResult Fallback(PhysicalGlyphFallbackReason reason) =>
        new(null, reason, false);
}
