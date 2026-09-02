using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace WSGM.Input;

/// <summary>The union/hold-timer chord state machine shared by the recorder and the
/// watcher, modelled on Handheld Companion's InputsManager: buttons accumulate into
/// a union that only clears on full release (so a combo does not need frame-perfect
/// presses), and a hold timer restarted on every state change promotes the chord to
/// "hold". State is tracked per physical pad: a chord must complete on ONE
/// controller — buttons held on another pad neither join the union nor block the
/// full-release detection.</summary>
internal sealed class ChordTracker : IDisposable
{
    /// <summary>Time with no state change before a held chord counts as a hold.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(600);

    /// <summary>Time with no input at all before recording gives up.</summary>
    public static readonly TimeSpan RecordingExpiry = TimeSpan.FromSeconds(3);

    /// <summary>One pad's chord episode. Union accumulates until full release.</summary>
    internal sealed class Pad
    {
        public GamepadButtons Union;
        /// <summary>Set by a consumer that acted on HoldElapsed so it does not act
        /// again (further state changes restart the hold timer) until full release.</summary>
        public bool HoldConsumed;
        public readonly DispatcherTimer HoldTimer = new() { Interval = Hold };
    }

    private readonly Dictionary<uint, Pad> _pads = new();

    /// <summary>The hold time elapsed with no state change on this pad. Can fire
    /// again after further state changes unless the consumer sets HoldConsumed.</summary>
    public event Action<Pad>? HoldElapsed;

    /// <summary>The pad was fully released; Union is the accumulated press chord.
    /// The pad's episode state resets after the handlers return.</summary>
    public event Action<Pad>? Released;

    /// <summary>Feed one pad's full state (from GamepadService.StateChanged).</summary>
    public void OnState(uint padId, GamepadButtons state)
    {
        if (!_pads.TryGetValue(padId, out var pad))
        {
            var newPad = new Pad();
            newPad.HoldTimer.Tick += (_, _) =>
            {
                newPad.HoldTimer.Stop();
                HoldElapsed?.Invoke(newPad);
            };
            _pads[padId] = pad = newPad;
        }

        // Every state change restarts the hold clock: it measures "time since the
        // last change", which is what lets a second button join the combo late.
        pad.HoldTimer.Stop();

        if (state != 0)
        {
            pad.Union |= state;         // union, cleared only on full release
            pad.HoldTimer.Start();
            return;
        }

        Released?.Invoke(pad);
        pad.Union = 0;
        pad.HoldConsumed = false;
        // Evict on full release: SDL hands every replug a fresh joystick instance
        // id, so after removal (GamepadService synthesizes a 0-state) this entry
        // could never be reused — keeping it would leak one Pad+timer per replug.
        // Safe for a still-connected pad too: the next press creates a fresh Pad
        // whose state (Union=0, HoldConsumed=false, timer stopped) is exactly the
        // post-release reset above, so chord accumulation semantics are unchanged.
        // The hold timer was stopped before this branch, so nothing keeps firing.
        _pads.Remove(padId);
    }

    /// <summary>Abandons every pad's in-flight episode.</summary>
    public void Reset()
    {
        foreach (var pad in _pads.Values)
        {
            pad.HoldTimer.Stop();
            pad.Union = 0;
            pad.HoldConsumed = false;
        }
    }

    public void Dispose() => Reset();
}
