using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// Projects display resolutions into the native quick-access menu and applies a chosen one.
/// </summary>
/// <remarks>
/// A thin adapter over <see cref="DisplayResolutionService"/>, which owns discovery, apply, and
/// restore. This layer exists only to speak the menu's shapes — strings the row can render and a
/// command result it can report — so the display policy stays in one place and is testable without
/// a menu.
/// </remarks>
internal sealed class NativeQamResolutionService
{
    private readonly DisplayResolutionService _display;

    /// <summary>Creates the adapter.</summary>
    /// <param name="display">The service that owns the display.</param>
    internal NativeQamResolutionService(DisplayResolutionService display)
        => _display = display ?? throw new ArgumentNullException(nameof(display));

    /// <summary>The row's current state.</summary>
    internal NativeQamResolutionState Current => Project(
        _display.Options(),
        DisplayProfiles.ReadCurrentResolution());

    /// <summary>Answers Steam's <c>setResolution</c> command.</summary>
    internal async Task<SteamUiCommandResult> HandleSetResolutionAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPayload.TryReadTarget(request.Payload, out string value))
        {
            return new(false, "The resolution payload is invalid.");
        }
        return await ApplyAsync(value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a resolution named as <c>WIDTHxHEIGHT</c>.</summary>
    /// <param name="value">The chosen option, exactly as the row offered it.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>Whether the display is now at that resolution.</returns>
    /// <remarks>
    /// Parsed rather than trusted: the value arrives from injected JavaScript, so an unparseable or
    /// unoffered string is refused here rather than reaching the driver. Applied off the calling
    /// thread because a mode change blocks.
    /// </remarks>
    internal async Task<SteamUiCommandResult> ApplyAsync(
        string value,
        CancellationToken cancellationToken)
    {
        if (!TryParse(value, out DisplayResolution resolution))
        {
            Log.Warn($"Native QAM resolution refused: '{value}' is not a resolution.");
            return new SteamUiCommandResult(false, "The resolution value is invalid.");
        }

        bool applied = await Task.Run(
            () => _display.Apply(resolution),
            cancellationToken).ConfigureAwait(false);
        return applied
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(false, $"The display refused {resolution}.");
    }

    /// <summary>Builds the row state from discovered options and the current mode.</summary>
    /// <param name="options">Resolutions the driver accepted.</param>
    /// <param name="current">The resolution in force, or null when unreadable.</param>
    /// <returns>The state the row renders.</returns>
    /// <remarks>
    /// Internal and pure so the row's availability rules are testable without a display. A single
    /// option is still no choice, so the row hides: offering a picker that cannot change anything
    /// reads as a broken control rather than an absent feature.
    /// </remarks>
    internal static NativeQamResolutionState Project(
        IReadOnlyList<DisplayResolution> options,
        DisplayResolution? current)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count < 2)
        {
            return new NativeQamResolutionState(
                false,
                [],
                current?.ToString() ?? string.Empty,
                options.Count == 0
                    ? "No display modes could be validated."
                    : "This display accepts only one resolution.");
        }

        return new NativeQamResolutionState(
            true,
            [.. options.Select(option => option.ToString())],
            current?.ToString() ?? string.Empty,
            string.Empty);
    }

    private static bool TryParse(string value, out DisplayResolution resolution)
    {
        resolution = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split('x', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int width)
            || !int.TryParse(parts[1], out int height)
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        resolution = new DisplayResolution(width, height);
        return true;
    }
}
