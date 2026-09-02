using System;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Where WSGM's own UI currently reads controller input.</summary>
internal enum UiInputSource
{
    /// <summary>The plugin's canonical physical input.</summary>
    ManagedCanonical,

    /// <summary>The SDL fallback while a focused surface owns the Steam Input lease.</summary>
    SdlWithSteamLease,
}

/// <summary>
/// The one source WSGM's own navigation subscribes to, whichever source is actually delivering.
/// </summary>
/// <remarks>
/// Managed canonical input supplies controls SDL does not expose, including rear paddles, Quick
/// Access, and trackpad clicks. SDL remains the fallback and keeps running while managed input is
/// active.
/// <para>
/// The switch is make-before-break, and the hard part is not the swap. It is the buttons held across
/// it: without explicit handling, a control held while the source changes produces a press edge on
/// the new source that the user never made, or a release that never arrives and leaves the control
/// latched.
/// </para>
/// </remarks>
internal sealed class UiInputRouter : IUiButtonSource, IDisposable
{
    /// <summary>Maximum time a control held across a source switch stays suppressed.</summary>
    /// <remarks>
    /// The incoming source may not expose every control the outgoing source saw. The timeout keeps
    /// an unobservable rear paddle from suppressing input forever while still covering an ordinary
    /// held control until its release arrives.
    /// </remarks>
    internal static TimeSpan HeldControlTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>The canonical-to-UI button map, in canonical order.</summary>
    private static readonly (CanonicalButtons Canonical, GamepadButtons Ui)[] Map =
    [
        (CanonicalButtons.DPadUp, GamepadButtons.DPadUp),
        (CanonicalButtons.DPadDown, GamepadButtons.DPadDown),
        (CanonicalButtons.DPadLeft, GamepadButtons.DPadLeft),
        (CanonicalButtons.DPadRight, GamepadButtons.DPadRight),
        (CanonicalButtons.Menu, GamepadButtons.Start),
        (CanonicalButtons.View, GamepadButtons.Back),
        (CanonicalButtons.LeftStick, GamepadButtons.LeftThumb),
        (CanonicalButtons.RightStick, GamepadButtons.RightThumb),
        (CanonicalButtons.LeftShoulder, GamepadButtons.LeftShoulder),
        (CanonicalButtons.RightShoulder, GamepadButtons.RightShoulder),
        (CanonicalButtons.A, GamepadButtons.A),
        (CanonicalButtons.B, GamepadButtons.B),
        (CanonicalButtons.X, GamepadButtons.X),
        (CanonicalButtons.Y, GamepadButtons.Y),
        (CanonicalButtons.RearPaddle1, GamepadButtons.L4),
        (CanonicalButtons.RearPaddle2, GamepadButtons.R4),
        (CanonicalButtons.RearPaddle3, GamepadButtons.L5),
        (CanonicalButtons.RearPaddle4, GamepadButtons.R5),
        (CanonicalButtons.Guide, GamepadButtons.Steam),
        (CanonicalButtons.QuickAccess, GamepadButtons.QuickAccess),
        (CanonicalButtons.LeftPadClick, GamepadButtons.LeftPadPress),
        (CanonicalButtons.RightPadClick, GamepadButtons.RightPadPress),
    ];

    /// <summary>How far a trigger travels before it counts as a press on the managed source.</summary>
    /// <remarks>
    /// NOT the SDL path's threshold: SdlGamepads synthesizes its trigger buttons at 8000/32767
    /// (about 0.24), so a trigger is easier to activate on SDL than here. The difference is
    /// long-shipped behavior; align only with device re-verification.
    /// </remarks>
    private const float TriggerThreshold = 0.5f;

    private readonly IUiButtonSource _fallback;
    private readonly TimeProvider _time;
    private UiInputSource _current = UiInputSource.SdlWithSteamLease;
    private GamepadButtons _suppressed;
    private GamepadButtons _managedHeld;
    private DateTimeOffset _switchedAt;
    private bool _managedHealthy;
    private bool _disposed;

    /// <summary>Creates the router over the always-present fallback source.</summary>
    /// <param name="fallback">The SDL source, which stays subscribed for the whole session.</param>
    /// <param name="timeProvider">Clock used to bound held-control suppression.</param>
    internal UiInputRouter(IUiButtonSource fallback, TimeProvider? timeProvider = null)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _time = timeProvider ?? TimeProvider.System;
        _fallback.ButtonPressed += OnFallbackPressed;
    }

    /// <inheritdoc/>
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>Which source WSGM's navigation is currently being driven by.</summary>
    internal UiInputSource Current => _current;

    /// <summary>Feeds one canonical sample from the plugin.</summary>
    /// <param name="sample">The sample the plugin published.</param>
    /// <remarks>
    /// The first sample is what makes the managed source healthy, which is the condition
    /// before the fallback is dropped: switching on "a managed source exists" rather than "it is
    /// delivering" leaves a gap in which nothing is delivering and the UI appears frozen.
    /// </remarks>
    internal void Submit(CanonicalControllerSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (_disposed)
        {
            return;
        }

        GamepadButtons held = Translate(sample);
        if (!_managedHealthy)
        {
            _managedHealthy = true;
            // Seeded from this sample, not from the accumulated held state: this is the first
            // sample the managed source has ever seen, so that state is empty and the mask would
            // capture nothing. A control the user was already holding when management came online
            // then arrived as a fresh press and could activate or dismiss whatever had focus.
            BeginSwitch(UiInputSource.ManagedCanonical, held);
        }

        // Edges rather than state, because that is what navigation acts on: SDL reports a press
        // once, and a canonical stream reports a held button on every sample. The held state keeps
        // tracking while the fallback is current, so it is already correct at the moment a later
        // switch happens rather than starting from nothing.
        GamepadButtons pressed = held & ~_managedHeld;
        _managedHeld = held;
        if (_current is not UiInputSource.ManagedCanonical)
        {
            return;
        }

        ReleaseSuppressed(held);
        // Neither a press edge nor a release edge is emitted for a suppressed control: the user
        // made neither, so reporting either would be inventing input.
        GamepadButtons allowed = pressed & ~_suppressed;
        if (allowed != 0)
        {
            ButtonPressed?.Invoke(allowed);
        }
    }

    /// <summary>Reports that the managed source has stopped delivering.</summary>
    /// <remarks>
    /// Called when controller management stops or faults. The fallback is already subscribed and
    /// running, so this is a break-after-make in the other direction and cannot leave a gap.
    /// </remarks>
    internal void ManagedSourceLost()
    {
        if (_disposed)
        {
            return;
        }

        if (!_managedHealthy)
        {
            Log.Change(
                "ui-input-source-lost",
                $"UI input source loss ignored: current={_current}, managedHealthy=false.");
            return;
        }

        _managedHealthy = false;
        BeginSwitch(UiInputSource.SdlWithSteamLease);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fallback.ButtonPressed -= OnFallbackPressed;
    }

    /// <summary>Switches the current source and suppresses whatever is held across the switch.</summary>
    /// <param name="to">The source taking over.</param>
    /// <param name="incomingHeld">
    /// Controls the incoming source reports as held right now, when the caller already knows them.
    /// Null falls back to what the managed source has accumulated.
    /// </param>
    private void BeginSwitch(UiInputSource to, GamepadButtons? incomingHeld = null)
    {
        if (_current == to)
        {
            Log.Change(
                "ui-input-source-switch",
                $"UI input source switch skipped: current={_current}, requested={to}.");
            return;
        }

        UiInputSource from = _current;
        // What the outgoing source had held is what must not produce edges on the incoming one.
        _suppressed = to is UiInputSource.ManagedCanonical
            ? incomingHeld ?? _managedHeld
            : 0;
        _switchedAt = _time.GetUtcNow();
        _current = to;
        if (to is not UiInputSource.ManagedCanonical)
        {
            // The managed source is no longer current, so its held state is stale. Leaving it would
            // swallow the first press after it comes back.
            _managedHeld = 0;
        }

        Log.Info(
            $"UI input source switched: from={from}, to={to}, "
                + $"suppressedButtons={_suppressed}, managedHealthy={_managedHealthy}.");
    }

    private void ReleaseSuppressed(GamepadButtons observedNow)
    {
        if (_suppressed == 0)
        {
            return;
        }

        // A control stays suppressed while the incoming source still reports it held, and is
        // released once observed up — or once the bound expires, for controls the incoming source
        // cannot see at all and would otherwise suppress forever.
        _suppressed = _time.GetUtcNow() - _switchedAt >= HeldControlTimeout
            ? 0
            : _suppressed & observedNow;
    }

    private void OnFallbackPressed(GamepadButtons buttons)
    {
        if (_current is UiInputSource.SdlWithSteamLease)
        {
            ButtonPressed?.Invoke(buttons);
        }
    }

    /// <summary>Translates one canonical sample into the UI button vocabulary.</summary>
    private static GamepadButtons Translate(CanonicalControllerSample sample)
    {
        GamepadButtons held = 0;
        foreach ((CanonicalButtons canonical, GamepadButtons ui) in Map)
        {
            if ((sample.Buttons & canonical) != 0)
            {
                held |= ui;
            }
        }

        if (sample.LeftTrigger >= TriggerThreshold)
        {
            held |= GamepadButtons.LeftTrigger;
        }

        if (sample.RightTrigger >= TriggerThreshold)
        {
            held |= GamepadButtons.RightTrigger;
        }

        return held;
    }
}
