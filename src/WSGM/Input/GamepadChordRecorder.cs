using System;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Records a controller chord: press one or more buttons (in any order) on
/// one pad and either release them (press chord) or keep holding (hold chord).</summary>
public sealed class GamepadChordRecorder : IDisposable
{
    private readonly GamepadService _gamepad;
    private readonly ChordTracker _tracker;
    private readonly DispatcherTimer _expiryTimer;
    private bool _recording;

    /// <summary>(buttons, isHold). Empty buttons = cancelled/timed out.</summary>
    public event Action<GamepadButtons, bool>? Recorded;

    /// <summary>Creates a recorder over an existing polling service, which stays the caller's:
    /// the recorder never stops or disposes it, so a second SDL poller never exists.</summary>
    /// <param name="gamepad">The caller's polling service.</param>
    public GamepadChordRecorder(GamepadService gamepad)
    {
        ArgumentNullException.ThrowIfNull(gamepad);
        _gamepad = gamepad;

        _tracker = new ChordTracker();
        _tracker.HoldElapsed += pad => Finish(pad.Union, isHold: true);
        _tracker.Released += pad => Finish(pad.Union, isHold: false);

        _expiryTimer = new DispatcherTimer { Interval = ChordTracker.RecordingExpiry };
        // The hold timer resolves any pressed buttons long before expiry and a full
        // release finishes immediately, so expiring can only mean no input at all.
        _expiryTimer.Tick += (_, _) => Finish(0, isHold: false, cancelled: true);
    }

    /// <summary>Begins recording the next complete chord from a single controller.</summary>
    public void Start()
    {
        _tracker.Reset();
        _recording = true;
        // Unsubscribe first so a Start() without an intervening Finish() cannot
        // stack a second subscription.
        _gamepad.StateChanged -= OnStateChanged;
        _gamepad.StateChanged += OnStateChanged;
        if (!_gamepad.IsRunning)
        {
            _gamepad.Start();
        }
        _expiryTimer.Stop();
        _expiryTimer.Start();
    }

    private void OnStateChanged(uint padId, GamepadButtons state)
    {
        if (!_recording)
        {
            return;
        }
        // Any input restarts the give-up clock.
        _expiryTimer.Stop();
        _expiryTimer.Start();
        _tracker.OnState(padId, state);
    }

    private void Finish(GamepadButtons union, bool isHold, bool cancelled = false)
    {
        if (!_recording)
        {
            return;
        }
        _recording = false;
        _expiryTimer.Stop();
        _tracker.Reset();
        _gamepad.StateChanged -= OnStateChanged;

        var buttons = cancelled ? 0 : union;
        Log.Info($"Recorded controller chord: {GamepadService.Describe(buttons, isHold)}");
        Recorded?.Invoke(buttons, isHold && buttons != 0);
    }

    /// <summary>Cancels recording and reports an empty chord.</summary>
    public void Cancel() => Finish(0, isHold: false, cancelled: true);

    /// <summary>Stops recording; the caller's polling service keeps running.</summary>
    public void Dispose()
    {
        _gamepad.StateChanged -= OnStateChanged;
        _expiryTimer.Stop();
        _tracker.Dispose();
    }
}
