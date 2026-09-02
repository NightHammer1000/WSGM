using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>
/// Offers the resolutions the driver accepts, applies one, and puts the original back.
/// </summary>
/// <remarks>
/// Separate from <see cref="RefreshRatePairingService"/> even though both move the display, because
/// they are owned by different things: the pairing service moves the refresh rate as a consequence
/// of a frame cap, while this moves the resolution because the user asked it to. Sharing one
/// captured original would let whichever restored second put back a mode the other had already
/// replaced.
/// <para>
/// Discovery is cached for the session, because enumerating and <c>CDS_TEST</c>-ing every mode is
/// not something to repeat while a menu is open.
/// </para>
/// </remarks>
internal sealed class DisplayResolutionService
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<DisplayResolution>> _discover;
    private readonly Func<int, int, bool> _apply;
    private readonly Func<DisplayResolution?> _readCurrent;
    private IReadOnlyList<DisplayResolution>? _accepted;
    private DisplayResolution? _original;

    /// <summary>Creates the service against the real display.</summary>
    internal DisplayResolutionService()
        : this(
            DisplayProfiles.EnumerateAcceptedResolutions,
            DisplayProfiles.TryApplyTransientResolution,
            DisplayProfiles.ReadCurrentResolution)
    {
    }

    /// <summary>Creates the service against supplied display operations.</summary>
    /// <param name="discover">Reads the resolutions the driver accepts.</param>
    /// <param name="apply">Applies one, reporting whether it took.</param>
    /// <param name="readCurrent">Reads the resolution in force, for restore.</param>
    /// <remarks>
    /// All three are injected, including the read: a service that reached the real display for even
    /// one of them could not be tested without one, and a machine with no display would answer null
    /// and change what restore does.
    /// </remarks>
    internal DisplayResolutionService(
        Func<IReadOnlyList<DisplayResolution>> discover,
        Func<int, int, bool> apply,
        Func<DisplayResolution?> readCurrent)
    {
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _readCurrent = readCurrent ?? throw new ArgumentNullException(nameof(readCurrent));
    }

    /// <summary>The resolutions worth offering.</summary>
    /// <returns>Accepted resolutions, ascending by pixel count.</returns>
    internal IReadOnlyList<DisplayResolution> Options()
    {
        lock (_gate)
        {
            _accepted ??= _discover();
            return _accepted;
        }
    }

    /// <summary>Applies a resolution, remembering the one to put back.</summary>
    /// <param name="resolution">The resolution to apply.</param>
    /// <returns>Whether the display is now at that resolution.</returns>
    /// <remarks>
    /// Refuses anything discovery did not accept rather than passing it to the driver. A resolution
    /// that was never validated is one the panel may not display at all, and recovering from a mode
    /// the user cannot see is not something to leave them to do.
    /// </remarks>
    internal bool Apply(DisplayResolution resolution)
    {
        if (!Options().Contains(resolution))
        {
            Log.Warn(
                $"Display resolution {resolution} refused: it is not among the accepted modes "
                + $"[{string.Join(",", Options())}].");
            return false;
        }

        CaptureOriginal();
        return _apply(resolution.Width, resolution.Height);
    }

    /// <summary>Puts back the resolution found before this service moved it.</summary>
    /// <returns><see langword="true"/> when nothing was left changed.</returns>
    /// <remarks>
    /// Applying is transient, so an abrupt exit already self-heals. This is for the ordinary case,
    /// where leaving the desktop at a game's resolution after it closes is a change the user never
    /// asked for and would have to hunt for.
    /// </remarks>
    internal bool Restore()
    {
        DisplayResolution? original;
        lock (_gate)
        {
            original = _original;
        }

        if (original is not { } resolution)
        {
            return true;
        }

        Log.Info($"Display resolution released; restoring {resolution}.");
        bool restored = _apply(resolution.Width, resolution.Height);
        if (restored)
        {
            lock (_gate)
            {
                if (_original == resolution)
                {
                    _original = null;
                }
            }
        }
        else
        {
            Log.Warn($"Display resolution could not restore {resolution}; the snapshot was retained.");
        }
        return restored;
    }

    private void CaptureOriginal()
    {
        lock (_gate)
        {
            // Captured once. Applying a second resolution before restoring must not overwrite the
            // user's own mode with the first applied one, or restore puts back a mode WSGM chose.
            _original ??= _readCurrent();
        }
    }
}
