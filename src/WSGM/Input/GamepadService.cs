using System;
using System.Collections.Generic;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Buttons WSGM can bind. The low 16 bits deliberately match XInput's
/// wButtons so the mapping is a straight cast; the high bits are what SDL reports
/// beyond XInput — analog triggers folded into buttons (any pad), plus the back
/// paddles, Steam and Quick Access buttons of Deck-class (real or emulated) pads.</summary>
[Flags]
public enum GamepadButtons : uint
{
    /// <summary>Up on the directional pad.</summary>
    DPadUp = 0x0001,
    /// <summary>Down on the directional pad.</summary>
    DPadDown = 0x0002,
    /// <summary>Left on the directional pad.</summary>
    DPadLeft = 0x0004,
    /// <summary>Right on the directional pad.</summary>
    DPadRight = 0x0008,
    /// <summary>The Menu or Start button.</summary>
    Start = 0x0010,
    /// <summary>The View, Back, or Select button.</summary>
    Back = 0x0020,
    /// <summary>Press on the left thumbstick.</summary>
    LeftThumb = 0x0040,
    /// <summary>Press on the right thumbstick.</summary>
    RightThumb = 0x0080,
    /// <summary>The left shoulder button.</summary>
    LeftShoulder = 0x0100,
    /// <summary>The right shoulder button.</summary>
    RightShoulder = 0x0200,
    /// <summary>The primary face button.</summary>
    A = 0x1000,
    /// <summary>The secondary face button.</summary>
    B = 0x2000,
    /// <summary>The left face button.</summary>
    X = 0x4000,
    /// <summary>The top face button.</summary>
    Y = 0x8000,

    // Beyond XInput's 16 bits. The triggers are synthesized from SDL's trigger
    // axes on any pad; only the rest need Deck-class hardware.
    /// <summary>The synthesized left analog trigger button.</summary>
    LeftTrigger = 0x0001_0000,
    /// <summary>The synthesized right analog trigger button.</summary>
    RightTrigger = 0x0002_0000,
    /// <summary>The upper-left rear paddle.</summary>
    L4 = 0x0004_0000,
    /// <summary>The upper-right rear paddle.</summary>
    R4 = 0x0008_0000,
    /// <summary>The lower-left rear paddle.</summary>
    L5 = 0x0010_0000,
    /// <summary>The lower-right rear paddle.</summary>
    R5 = 0x0020_0000,
    /// <summary>The Steam or guide button.</summary>
    Steam = 0x0040_0000,
    /// <summary>The Quick Access button.</summary>
    QuickAccess = 0x0080_0000,
    /// <summary>Press on the left touchpad.</summary>
    LeftPadPress = 0x0100_0000,
    /// <summary>Press on the right touchpad.</summary>
    RightPadPress = 0x0200_0000,
}

/// <summary>Polls all connected controllers through SDL3 on the UI thread while
/// enabled. Emits edge-triggered button events with D-pad/stick auto-repeat.</summary>
public sealed class GamepadService : IUiButtonSource, IDisposable
{
    // Monotonic (Environment.TickCount64) rather than wall-clock deadlines: a
    // backward system-clock adjustment — w32time resyncing shortly after logon,
    // or a resume from Modern Standby — would otherwise leave the next repeat
    // parked in the future and the D-pad would silently stop repeating.
    private const long RepeatInitialMs = 400;
    private const long RepeatRateMs = 150;
    private const GamepadButtons DirectionMask = GamepadButtons.DPadUp | GamepadButtons.DPadDown |
                                                 GamepadButtons.DPadLeft | GamepadButtons.DPadRight;

    private readonly DispatcherTimer _timer;
    /// <summary>Last observed state per pad id. Edges and chords are evaluated per
    /// pad so one controller holding a button cannot mask or complete another's.</summary>
    private readonly Dictionary<uint, GamepadButtons> _perPad = new();
    private readonly List<uint> _stalePads = new();
    private GamepadButtons _repeating;
    private long _nextRepeat;
    private bool _loggedFirstPress;

    /// <summary>Newly pressed buttons across all pads (edge-triggered per pad),
    /// with auto-repeat for directions.</summary>
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>One pad's full button state, raised whenever it changes. Chord
    /// detection needs the whole state per physical pad, not just the new edges.</summary>
    public event Action<uint, GamepadButtons>? StateChanged;

    /// <summary>Creates an inactive UI-thread polling service.</summary>
    public GamepadService()
    {
        // The convenience ctor taking a callback auto-starts the timer, which made
        // IsRunning permanently true and broke every "start if not running" guard.
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Initializes SDL, clears stale controller state, and begins polling.
    /// A no-op while already polling.</summary>
    public void Start()
    {
        if (_timer.IsEnabled)
        {
            // Already polling. Clearing the per-pad state here would make the next
            // 16 ms tick report every STILL-HELD button as a fresh press: a hold
            // chord that opened a surface while its buttons are down would confirm
            // or dismiss that surface immediately.
            return;
        }
        _perPad.Clear();
        _repeating = 0;
        _loggedFirstPress = false;
        SdlGamepads.EnsureInitialized();
        _timer.Start();
        Log.Info("Gamepad polling started.");
    }

    /// <summary>Stops polling without shutting down SDL's process-wide state.</summary>
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var pads = SdlGamepads.Update();

        GamepadButtons current = 0;
        GamepadButtons pressed = 0;
        foreach (var pad in pads)
        {
            current |= pad.Buttons;
            _perPad.TryGetValue(pad.Id, out var previous);
            // Edge-trigger per pad: pad A holding a button must not mask pad B
            // freshly pressing the same button.
            pressed |= pad.Buttons & ~previous;
            if (pad.Buttons != previous)
            {
                _perPad[pad.Id] = pad.Buttons;
                StateChanged?.Invoke(pad.Id, pad.Buttons);
            }
        }

        // A pad unplugged mid-chord counts as a full release, so its chord state
        // downstream can't stay stuck holding phantom buttons.
        _stalePads.Clear();
        foreach (var (id, _) in _perPad)
        {
            var present = false;
            foreach (var pad in pads)
            {
                if (pad.Id == id)
                {
                    present = true;
                    break;
                }
            }
            if (!present)
            {
                _stalePads.Add(id);
            }
        }
        foreach (var id in _stalePads)
        {
            var previous = _perPad[id];
            _perPad.Remove(id);
            if (previous != 0)
            {
                StateChanged?.Invoke(id, 0);
            }
        }

        if (pressed != 0)
        {
            if (!_loggedFirstPress)
            {
                // One line per Start() so a pasted log proves input arrives at all.
                _loggedFirstPress = true;
                Log.Info($"Controller input: {Describe(pressed, false)}");
            }
            ButtonPressed?.Invoke(pressed);
        }

        // Auto-repeat for held directions (any pad).
        var directions = current & DirectionMask;
        if (directions != 0)
        {
            var newDirections = pressed & DirectionMask;
            if (newDirections != 0)
            {
                // A fresh press re-arms the repeat and becomes the repeated
                // direction, so a diagonal repeats the direction that initiated it
                // instead of the whole held set (which navigation resolves as Next).
                _repeating = newDirections;
                _nextRepeat = Environment.TickCount64 + RepeatInitialMs;
            }
            else if ((directions & _repeating) == 0)
            {
                // The repeated direction was released but another is still held
                // (diagonal released in the other order): re-arm on what remains.
                _repeating = directions;
                _nextRepeat = Environment.TickCount64 + RepeatInitialMs;
            }
            else if (Environment.TickCount64 >= _nextRepeat)
            {
                _nextRepeat = Environment.TickCount64 + RepeatRateMs;
                ButtonPressed?.Invoke(directions & _repeating);
            }
        }
        else
        {
            _repeating = 0;
        }
    }

    /// <summary>Gets whether the UI-thread polling timer is active.</summary>
    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Formats a button combination for display, e.g. "Hold LB + Start".</summary>
    /// <param name="buttons">The buttons to render.</param>
    /// <param name="hold">Whether to prefix the result with <c>Hold</c>.</param>
    /// <returns>A user-facing chord description, or <c>None</c> for no buttons.</returns>
    public static string Describe(GamepadButtons buttons, bool hold)
    {
        if (buttons == 0)
        {
            return "None";
        }
        var names = new List<string>();
        foreach (var (flag, name) in ButtonNames)
        {
            if (buttons.HasFlag(flag))
            {
                names.Add(name);
            }
        }
        var combo = string.Join(" + ", names);
        return hold ? $"Hold {combo}" : combo;
    }

    private static readonly (GamepadButtons Flag, string Name)[] ButtonNames =
    [
        (GamepadButtons.A, "A"), (GamepadButtons.B, "B"), (GamepadButtons.X, "X"), (GamepadButtons.Y, "Y"),
        (GamepadButtons.LeftShoulder, "LB"), (GamepadButtons.RightShoulder, "RB"),
        (GamepadButtons.LeftThumb, "L3"), (GamepadButtons.RightThumb, "R3"),
        (GamepadButtons.Start, "Start"), (GamepadButtons.Back, "Back"),
        (GamepadButtons.DPadUp, "D-Up"), (GamepadButtons.DPadDown, "D-Down"),
        (GamepadButtons.DPadLeft, "D-Left"), (GamepadButtons.DPadRight, "D-Right"),
        (GamepadButtons.LeftTrigger, "L2"), (GamepadButtons.RightTrigger, "R2"),
        (GamepadButtons.L4, "L4"), (GamepadButtons.R4, "R4"),
        (GamepadButtons.L5, "L5"), (GamepadButtons.R5, "R5"),
        (GamepadButtons.Steam, "Steam"), (GamepadButtons.QuickAccess, "Quick Access"),
        (GamepadButtons.LeftPadPress, "L-Pad"), (GamepadButtons.RightPadPress, "R-Pad"),
    ];

    /// <summary>Stops this service's timer. SDL stays initialized process-wide.</summary>
    public void Dispose() => _timer.Stop();
}
