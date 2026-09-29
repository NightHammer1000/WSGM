using System.Collections.Generic;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Translates Steam's <c>perf_overlay_level</c> wire values to and from WSGM's notch
/// order.</summary>
/// <remarks>
/// Valve added the Minimal preset last, so <c>EGraphicsPerfOverlayLevel</c> is Hidden=0, Basic=1,
/// Medium=2, Full=3, Minimal=4 — while the selector presents OFF, Minimal, Basic, Medium, Full.
/// WSGM's semantic level is the NOTCH: 0 off, 1–3 the fixed WSGM-rendered OSD presets, 4 the
/// user-configured Custom layout.
/// Treating the wire value as the notch put the top level on the first notch and shifted the rest
/// (live-verified 2026-09-01: parking the selector on notch 1 stores
/// <c>perf_overlay_level=4</c>). Both QAM boundary directions translate; everything behind the
/// boundary — policy, adapter, OSD renderer — speaks notches.
/// </remarks>
internal static class NativeQamOverlayLevelWire
{
    /// <summary>Maps a Steam wire value to the selector notch WSGM's levels are defined on.</summary>
    /// <param name="steamValue">The <c>perf_overlay_level</c> value Steam sent.</param>
    /// <returns>The notch, with unknown values reading as off.</returns>
    internal static int ToNotch(int steamValue) => steamValue switch
    {
        4 => 1,
        1 => 2,
        2 => 3,
        3 => 4,
        _ => 0,
    };

    /// <summary>Maps a WSGM notch to the Steam wire value the selector resolves.</summary>
    /// <param name="notch">The WSGM semantic level.</param>
    /// <returns>The wire value, with unknown notches reading as hidden.</returns>
    internal static int ToSteam(int notch) => notch switch
    {
        1 => 4,
        2 => 1,
        3 => 2,
        4 => 3,
        _ => 0,
    };
}

/// <summary>One setting change Steam's performance panel asked WSGM to make.</summary>
/// <param name="Kind">Which setting changed.</param>
/// <param name="Value">The requested value; meaning depends on <paramref name="Kind"/>.</param>
internal readonly record struct NativeQamPerfChange(NativeQamPerfSetting Kind, int Value)
{
    /// <summary>Reads the change as a flag.</summary>
    internal bool AsFlag => Value != 0;
}

/// <summary>The performance settings WSGM accepts writes for.</summary>
/// <remarks>
/// Only the settings behind a control WSGM actually mounts. A delta naming anything else is
/// reported as unsupported rather than silently dropped, because a control that appears to work and
/// does nothing is worse than one that is not there.
/// </remarks>
internal enum NativeQamPerfSetting
{
    /// <summary>The frame cap in FPS.</summary>
    FrameLimit,

    /// <summary>Whether the frame cap applies.</summary>
    FrameLimitEnabled,

    /// <summary>The performance overlay level.</summary>
    OverlayLevel,

    /// <summary>Whether variable refresh rate is on.</summary>
    VariableRefreshRate,

    /// <summary>The manually chosen refresh rate in Hz.</summary>
    RefreshRateHz,

    /// <summary>Whether the running application keeps its own profile.</summary>
    PerApplicationProfileEnabled,

    /// <summary>Whether the advanced rows are shown.</summary>
    AdvancedSettingsEnabled,
}

/// <summary>What one <c>UpdateSettings</c> call asked for.</summary>
/// <param name="Recognized">Changes WSGM can apply, in the order they appeared.</param>
/// <param name="ResetToDefault">Whether the panel asked to reset the current profile.</param>
/// <param name="SteamAppId">The AppID the delta targets, or null for the global profile.</param>
/// <param name="Unsupported">
/// Field names that were present and are not backed, for the log. Never empty silently.
/// </param>
internal sealed record NativeQamPerfDelta(
    IReadOnlyList<NativeQamPerfChange> Recognized,
    bool ResetToDefault,
    uint? SteamAppId,
    IReadOnlyList<string> Unsupported);

/// <summary>
/// Decodes a <c>CMsgSystemPerfUpdateSettings</c> that the injected shim forwarded as an object.
/// </summary>
/// <remarks>
/// Every setter in Valve's store builds a delta and hands it to the one <c>UpdateSettings</c>
/// method, so this is where all of them arrive. The message shapes belong to the client, so the
/// injected half forwards <c>toObject()</c> verbatim and this half does the interpreting; nothing
/// about the wire format is reimplemented on either side.
/// <para>
/// A delta carries only what changed, and a settings message nests
/// <c>settings_delta.global</c>/<c>settings_delta.per_app</c>. Both are optional and either may be
/// absent on any given call.
/// </para>
/// </remarks>
internal static class NativeQamPerfDeltaReader
{
    /// <summary>Reads a forwarded update-settings payload.</summary>
    /// <param name="payload">The request payload, expected to carry a <c>delta</c> object.</param>
    /// <param name="delta">The decoded delta when this returns true.</param>
    /// <param name="error">Why the payload could not be read, when this returns false.</param>
    /// <returns>Whether the payload was a readable delta.</returns>
    internal static bool TryRead(
        JsonElement payload,
        out NativeQamPerfDelta delta,
        out string? error)
    {
        delta = new NativeQamPerfDelta([], false, null, []);
        error = null;

        if (payload.ValueKind is not JsonValueKind.Object
            || !payload.TryGetProperty("delta", out JsonElement message))
        {
            error = "The performance delta payload carried no delta object.";
            return false;
        }

        if (message.ValueKind is JsonValueKind.String)
        {
            // Named separately because it is one specific regression, not a malformed payload:
            // every SystemPerfStore setter calls UpdateSettings with serializeBase64String(), so a
            // string here means the injected shim stopped decoding it and EVERY performance control
            // has silently stopped working. Saying so beats "no delta object".
            error = "The performance delta arrived undecoded; the injected shim did not deserialize "
                + "the update-settings message.";
            return false;
        }

        if (message.ValueKind is not JsonValueKind.Object)
        {
            error = "The performance delta payload carried no delta object.";
            return false;
        }

        List<NativeQamPerfChange> recognized = [];
        List<string> unsupported = [];

        bool resetToDefault = ReadFlag(message, "reset_to_default") ?? false;
        uint? steamAppId = ReadAppId(message);

        if (message.TryGetProperty("settings_delta", out JsonElement settings)
            && settings.ValueKind is JsonValueKind.Object)
        {
            if (settings.TryGetProperty("global", out JsonElement global)
                && global.ValueKind is JsonValueKind.Object)
            {
                ReadFields(global, recognized, unsupported);
            }

            if (settings.TryGetProperty("per_app", out JsonElement perApp)
                && perApp.ValueKind is JsonValueKind.Object)
            {
                ReadFields(perApp, recognized, unsupported);
            }
        }

        delta = new NativeQamPerfDelta(recognized, resetToDefault, steamAppId, unsupported);
        return true;
    }

    private static void ReadFields(
        JsonElement settings,
        List<NativeQamPerfChange> recognized,
        List<string> unsupported)
    {
        foreach (JsonProperty property in settings.EnumerateObject())
        {
            // toObject() emits every field of the message, not only the ones the setter touched, so
            // a null or absent value is "not part of this delta" and must not be applied. Treating
            // them as changes would make one slider write every other control's current value back
            // on every drag.
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            NativeQamPerfSetting? kind = property.Name switch
            {
                // The `_external` names are the same settings, written by the same controls when
                // Steam classifies the panel's display as external — which the Claw's built-in one
                // reports itself as. A delta carries one twin or the other, never both, because the
                // control reads and writes whichever side its own display test selected.
                "fps_limit" or "fps_limit_external" => NativeQamPerfSetting.FrameLimit,
                "is_fps_limit_enabled" => NativeQamPerfSetting.FrameLimitEnabled,
                "perf_overlay_level" => NativeQamPerfSetting.OverlayLevel,
                "is_vrr_enabled" => NativeQamPerfSetting.VariableRefreshRate,
                "display_refresh_manual_hz" or "display_external_refresh_manual_hz" =>
                    NativeQamPerfSetting.RefreshRateHz,
                "is_game_perf_profile_enabled" => NativeQamPerfSetting.PerApplicationProfileEnabled,
                "is_advanced_settings_enabled" => NativeQamPerfSetting.AdvancedSettingsEnabled,
                _ => null,
            };

            if (kind is not { } setting)
            {
                unsupported.Add(property.Name);
                continue;
            }

            if (TryReadInteger(property.Value, out int value))
            {
                recognized.Add(new NativeQamPerfChange(setting, value));
            }
            else
            {
                unsupported.Add(property.Name);
            }
        }
    }

    private static bool TryReadInteger(JsonElement value, out int result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                result = 1;
                return true;
            case JsonValueKind.False:
                result = 0;
                return true;
            case JsonValueKind.Number when value.TryGetInt32(out result):
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool? ReadFlag(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>The Steam client's own pseudo-game id, Valve's vocabulary for the global profile.</summary>
    /// <remarks>
    /// Every store setter stamps <c>gameid</c> from the current or active profile game id, and WSGM
    /// publishes 769 for both whenever no per-game profile is in force, so a global-profile write
    /// arrives carrying 769. Reading it as a real AppID would refuse every one of those writes as
    /// stale against a session that has no running application.
    /// </remarks>
    private const ulong SteamClientPseudoGameId = 769;

    /// <remarks>
    /// <c>gameid</c> is a 64-bit id, and the client emits it as either a number or a string
    /// depending on magnitude. Anything that is not a Steam AppID — zero, the Steam client's own
    /// pseudo-app, or a value beyond 32 bits such as a full game id — targets the global profile
    /// rather than being guessed at.
    /// </remarks>
    private static uint? ReadAppId(JsonElement message)
    {
        if (!message.TryGetProperty("gameid", out JsonElement value))
        {
            return null;
        }

        ulong raw = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out ulong number) => number,
            JsonValueKind.String when ulong.TryParse(value.GetString(), out ulong parsed) => parsed,
            _ => 0,
        };

        return raw is > 0 and <= uint.MaxValue && raw != SteamClientPseudoGameId
            ? (uint)raw
            : null;
    }
}
