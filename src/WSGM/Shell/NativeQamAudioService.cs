using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One audio endpoint as Steam's own device picker renders it.</summary>
/// <param name="Id">Stable endpoint identifier.</param>
/// <param name="Name">Endpoint name as Windows reports it.</param>
/// <param name="HasOutput">Whether the endpoint can render.</param>
/// <param name="HasInput">Whether the endpoint can capture.</param>
internal sealed record NativeQamAudioDevice(
    string Id,
    string Name,
    bool HasOutput,
    bool HasInput
);

/// <summary>Audio as Steam's own menu renders it.</summary>
/// <remarks>
/// Volume and mute follow Windows' default endpoint independently for render and capture. Steam's
/// model allows values per device, but reporting a value for an inactive endpoint WSGM cannot
/// independently move would be a control that lies.
/// </remarks>
/// <param name="Available">Whether audio can be observed and changed at all.</param>
/// <param name="Devices">Every endpoint, output and input.</param>
/// <param name="ActiveOutputDeviceId">The default render endpoint, or empty.</param>
/// <param name="ActiveInputDeviceId">The default capture endpoint, or empty.</param>
/// <param name="VolumePercent">System volume, 0-100.</param>
/// <param name="Muted">Whether the default render endpoint is muted.</param>
/// <param name="InputVolumePercent">Default capture volume, 0-100, or null when unavailable.</param>
/// <param name="InputMuted">Whether the default capture endpoint is muted.</param>
/// <param name="StatusText">A human-readable fault, or empty.</param>
internal sealed record NativeQamAudioState(
    bool Available,
    IReadOnlyList<NativeQamAudioDevice> Devices,
    string ActiveOutputDeviceId,
    string ActiveInputDeviceId,
    int VolumePercent,
    bool Muted,
    int? InputVolumePercent,
    bool InputMuted,
    string StatusText
);

/// <summary>
/// Projects <see cref="AudioManager"/> into the shape Steam's audio store expects.
/// </summary>
/// <remarks>
/// The backend already exists and is the same one the custom taskbar drives, so this is an adapter
/// rather than an implementation. Keeping it an adapter is the point: a second audio path would
/// eventually disagree with the taskbar about which endpoint is default.
/// </remarks>
internal sealed class AudioManagerNativeQamAudioService : IDisposable
{
    private readonly AudioManager _audio;
    private readonly object _gate = new();
    private NativeQamAudioState _current;
    private bool _disposed;

    /// <summary>Adapts a running audio manager.</summary>
    /// <param name="audio">The manager, already started.</param>
    public AudioManagerNativeQamAudioService(AudioManager audio)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _current = Project(audio);
        _audio.PropertyChanged += OnAudioChanged;
        _audio.OutputEndpoints.CollectionChanged += OnEndpointsChanged;
        _audio.InputEndpoints.CollectionChanged += OnEndpointsChanged;
    }

    /// <summary>Raised when the projected state changes.</summary>
    public event Action? StateChanged;

    /// <summary>The state Steam should currently be rendering.</summary>
    public NativeQamAudioState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Answers Steam's <c>getDevices</c> command with the current state.</summary>
    internal Task<SteamUiCommandResult> HandleGetDevicesAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SteamUiCommandResult(true, null, SerializeState(Current)));

    /// <summary>Answers Steam's <c>setDefaultDevice</c> command.</summary>
    internal async Task<SteamUiCommandResult> HandleSetDefaultDeviceAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadDevicePayload(request.Payload, out string id, out bool input))
        {
            return new(false, "The audio device payload is invalid.");
        }
        return await SetDefaultDeviceAsync(id, input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers Steam's <c>setVolume</c> command.</summary>
    internal async Task<SteamUiCommandResult> HandleSetVolumeAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadVolumePayload(request.Payload, out int percent, out bool input))
        {
            return new(false, "The audio volume payload is invalid.");
        }
        return await SetVolumeAsync(percent, input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes one endpoint the default for its direction.</summary>
    /// <param name="deviceId">The endpoint to select.</param>
    /// <param name="input">Whether the capture default is being set rather than the render one.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The outcome, as the native QAM reports outcomes.</returns>
    public async Task<SteamUiCommandResult> SetDefaultDeviceAsync(
        string deviceId,
        bool input,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            Log.Warn("Native QAM audio: default device change refused; no endpoint named.");
            return new SteamUiCommandResult(false, "No audio device was named.");
        }

        AudioEndpointEntry? entry = null;
        await NativeQamUi.RunAsync(() =>
        {
            entry = (input ? _audio.InputEndpoints : _audio.OutputEndpoints)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, deviceId, StringComparison.Ordinal));
            if (entry is null)
            {
                return;
            }

            if (input)
            {
                _audio.SelectedInput = entry;
            }
            else
            {
                _audio.SelectedOutput = entry;
            }

            Publish();
        }, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            Log.Warn(
                $"Native QAM audio: '{deviceId}' is not a known "
                + $"{(input ? "input" : "output")} endpoint.");
            return new SteamUiCommandResult(false, "That audio device is no longer present.");
        }

        Log.Info(
            $"Native QAM audio: default {(input ? "input" : "output")} set to '{entry.Name}'.");
        return new SteamUiCommandResult(true, string.Empty);
    }

    /// <summary>Sets the system volume.</summary>
    /// <param name="percent">Target volume, 0-100.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The outcome, as the native QAM reports outcomes.</returns>
    public async Task<SteamUiCommandResult> SetVolumeAsync(
        int percent,
        CancellationToken cancellationToken
    ) => await SetVolumeAsync(percent, input: false, cancellationToken).ConfigureAwait(false);

    /// <summary>Sets the master volume for one audio direction.</summary>
    /// <param name="percent">Target volume, 0-100.</param>
    /// <param name="input">Whether to set the default capture endpoint.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The outcome, as the native QAM reports outcomes.</returns>
    public async Task<SteamUiCommandResult> SetVolumeAsync(
        int percent,
        bool input,
        CancellationToken cancellationToken
    )
    {
        int clamped = Math.Clamp(percent, 0, 100);
        if (clamped != percent)
        {
            Log.Info(
                $"Native QAM audio: {(input ? "microphone " : string.Empty)}volume "
                    + $"{percent} clamped to {clamped}.");
        }

        await NativeQamUi.RunAsync(() =>
        {
            if (input)
            {
                _audio.InputVolumePercent = clamped;
            }
            else
            {
                _audio.VolumePercent = clamped;
            }
            Publish();
        }, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, string.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _audio.PropertyChanged -= OnAudioChanged;
        _audio.OutputEndpoints.CollectionChanged -= OnEndpointsChanged;
        _audio.InputEndpoints.CollectionChanged -= OnEndpointsChanged;
    }

    /// <summary>Serializes the audio state into the shape the injected bootstrap maps.</summary>
    /// <remarks>
    /// The device shape here is deliberately WSGM's own; the bootstrap maps it into Steam's field
    /// names. Emitting Steam's names on this side would put its schema in two places, and the one
    /// that changes with a client rebuild is the injected half.
    /// </remarks>
    internal static JsonElement SerializeState(NativeQamAudioState state) =>
        JsonSerializer.SerializeToElement(
            state,
            NativeQamSemanticJsonContext.Default.NativeQamAudioState);

    /// <summary>Reads the endpoint and direction of a default-device change.</summary>
    private static bool TryReadDevicePayload(JsonElement payload, out string id, out bool input)
    {
        input = false;
        if (!NativeQamPayload.TryReadBoundedString(payload, "id", 512, out id)
            || !payload.TryGetProperty("input", out JsonElement inputProperty)
            || inputProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !NativeQamPayload.HasExactly(payload, 2))
        {
            return false;
        }

        input = inputProperty.ValueKind is JsonValueKind.True;
        return true;
    }

    private static bool TryReadVolumePayload(JsonElement payload, out int percent, out bool input)
    {
        input = false;
        if (!NativeQamPayload.TryReadInt(payload, "percent", 0, 100, out percent))
        {
            return false;
        }

        if (!payload.TryGetProperty("input", out JsonElement inputProperty))
        {
            return NativeQamPayload.HasExactly(payload, 1);
        }
        if (inputProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        input = inputProperty.ValueKind is JsonValueKind.True;
        return NativeQamPayload.HasExactly(payload, 2);
    }

    /// <summary>
    /// Builds the state Steam should render from the manager's current view.
    /// </summary>
    /// <param name="audio">The manager to read.</param>
    /// <returns>The projected state.</returns>
    /// <remarks>
    /// An endpoint present in both directions is reported once carrying both flags, because Steam's
    /// device model is one entry with a direction test rather than two entries. Listing it twice
    /// would put the same hardware in the picker under two identities.
    /// </remarks>
    internal static NativeQamAudioState Project(AudioManager audio)
    {
        Dictionary<string, NativeQamAudioDevice> byId = new(StringComparer.Ordinal);
        foreach (AudioEndpointEntry entry in audio.OutputEndpoints)
        {
            byId[entry.Id] = new NativeQamAudioDevice(entry.Id, entry.Name, true, false);
        }

        foreach (AudioEndpointEntry entry in audio.InputEndpoints)
        {
            byId[entry.Id] = byId.TryGetValue(entry.Id, out NativeQamAudioDevice? existing)
                ? existing with { HasInput = true }
                : new NativeQamAudioDevice(entry.Id, entry.Name, false, true);
        }

        bool available = byId.Count > 0;
        return new NativeQamAudioState(
            available,
            [.. byId.Values],
            audio.SelectedOutput?.Id ?? string.Empty,
            audio.SelectedInput?.Id ?? string.Empty,
            (int)Math.Round(audio.VolumePercent),
            audio.Muted,
            audio.InputVolumePercent is { } inputVolume
                ? (int)Math.Round(inputVolume)
                : null,
            audio.InputMuted,
            available ? audio.ErrorText : "No audio endpoints are present.");
    }

    private void OnAudioChanged(object? sender, PropertyChangedEventArgs e) => Publish();

    private void OnEndpointsChanged(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        NativeQamAudioState next = Project(_audio);
        lock (_gate)
        {
            if (Same(_current, next))
            {
                return;
            }

            _current = next;
        }

        StateChanged?.Invoke();
    }

    /// <remarks>
    /// The device list has to be compared element by element. Record equality would compare it by
    /// reference, and <see cref="Project"/> builds a fresh list every time, so every property change
    /// on the manager — including a volume tick — would look like a change to the whole device set
    /// and push a redundant update into Steam.
    /// </remarks>
    private static bool Same(NativeQamAudioState left, NativeQamAudioState right) =>
        left.Available == right.Available
        && left.VolumePercent == right.VolumePercent
        && left.Muted == right.Muted
        && left.InputVolumePercent == right.InputVolumePercent
        && left.InputMuted == right.InputMuted
        && string.Equals(left.ActiveOutputDeviceId, right.ActiveOutputDeviceId, StringComparison.Ordinal)
        && string.Equals(left.ActiveInputDeviceId, right.ActiveInputDeviceId, StringComparison.Ordinal)
        && string.Equals(left.StatusText, right.StatusText, StringComparison.Ordinal)
        && left.Devices.SequenceEqual(right.Devices);
}
