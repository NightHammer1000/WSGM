using System;
using System.Collections.Generic;
using SDL;
using WSGM.Core;
using static SDL.SDL3;

namespace WSGM.Input;

/// <summary>Process-wide owner of all SDL3 gamepad interop. A single event pump is
/// mandatory: two GamepadService instances can exist at once (overlay + settings),
/// and if each called SDL_PollEvent they would steal hotplug events from the other.
/// All members must be called from the UI thread. SDL is initialized once and never
/// quit: owners have independent lifecycles and SDL's reads are shared/non-exclusive,
/// so a running game is unaffected.</summary>
internal static unsafe class SdlGamepads
{
    /// <summary>One pad's folded button state, keyed by SDL joystick instance id.
    /// Per-pad states let chord detection require a chord to complete on ONE
    /// physical pad instead of being assembled from buttons across controllers.</summary>
    public readonly record struct PadSnapshot
    {
        /// <summary>Creates a per-controller button snapshot.</summary>
        /// <param name="id">SDL's stable controller identifier for the current connection.</param>
        /// <param name="buttons">The normalized buttons currently held on that controller.</param>
        public PadSnapshot(uint id, GamepadButtons buttons)
        {
            Id = id;
            Buttons = buttons;
        }

        /// <summary>Gets SDL's stable identifier for the connected controller.</summary>
        public uint Id { get; }

        /// <summary>Gets the normalized buttons currently held on the controller.</summary>
        public GamepadButtons Buttons { get; }
    }

    private static bool _initialized;
    private static bool _failed;
    private static readonly Dictionary<SDL_JoystickID, nint> Pads = new();
    private static readonly List<PadSnapshot> Snapshot = new();

    private const short StickDeadzone = 16000;
    private const short TriggerThreshold = 8000; // axis range is 0..32767

    private static readonly (SDL_GamepadButton Sdl, GamepadButtons Flag)[] ButtonMap =
    [
        // Positional, identical semantics to XInput's wButtons.
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, GamepadButtons.A),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, GamepadButtons.B),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, GamepadButtons.X),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, GamepadButtons.Y),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, GamepadButtons.DPadUp),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, GamepadButtons.DPadDown),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, GamepadButtons.DPadLeft),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, GamepadButtons.DPadRight),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, GamepadButtons.Start),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, GamepadButtons.Back),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, GamepadButtons.LeftThumb),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, GamepadButtons.RightThumb),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, GamepadButtons.LeftShoulder),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, GamepadButtons.RightShoulder),
        // Xbox Guide, the Steam/PS button on Valve/Sony pads.
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE, GamepadButtons.Steam),
        // Deck QAM (misc1); capture/mic button on other pads — still bindable.
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_MISC1, GamepadButtons.QuickAccess),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE1, GamepadButtons.L4),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE2, GamepadButtons.L5),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1, GamepadButtons.R4),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE2, GamepadButtons.R5),
        // Deck trackpad clicks (best-effort: depends on the bundled SDL version's
        // Deck mapping; on DS4/DualSense the single pad click lands on TOUCHPAD).
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_TOUCHPAD, GamepadButtons.LeftPadPress),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_MISC2, GamepadButtons.RightPadPress),
    ];

    /// <summary>Initializes SDL's gamepad subsystem once for the process.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized || _failed)
        {
            return;
        }

        try
        {
            // Must be set before init. WSGM has no SDL window, and the chord
            // watcher must see the pad while a game holds the foreground — without
            // this hint SDL drops input whenever no SDL window is focused, which
            // for WSGM is always.
            SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
            // Real Steam Controller grips (parity with the old Valve HID reader).
            SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_STEAM, "1");

            if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_GAMEPAD))
            {
                // Degrade to keyboard/touch instead of taking the shell down.
                Log.Error($"SDL_InitSubSystem(GAMEPAD) failed: {SDL_GetError()}");
                _failed = true;
                return;
            }
        }
        catch (Exception ex)
        {
            // Missing/corrupt SDL3.dll throws at the first P/Invoke; the shell must
            // survive on keyboard/touch.
            Log.Error("SDL3 unavailable, controller input disabled", ex);
            _failed = true;
            return;
        }

        _initialized = true;
        var v = SDL_GetVersion();
        Log.Info($"SDL {v / 1000000}.{v / 1000 % 1000}.{v % 1000} gamepad subsystem initialized.");

        int count;
        var ids = SDL_GetGamepads(&count);
        if (ids != null)
        {
            for (var i = 0; i < count; i++)
            {
                OpenPad(ids[i]);
            }
            SDL_free(ids);
        }
    }

    /// <summary>Pumps SDL events (hotplug) and returns each pad's state, with the
    /// left stick folded into the D-pad flags and triggers as buttons. The returned
    /// list is reused across calls — consume it before the next Update().</summary>
    public static IReadOnlyList<PadSnapshot> Update()
    {
        Snapshot.Clear();
        if (!_initialized)
        {
            return Snapshot;
        }

        SDL_Event e;
        while (SDL_PollEvent(&e))
        {
            switch (e.Type)
            {
                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                    OpenPad(e.gdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    ClosePad(e.gdevice.which);
                    break;
            }
        }

        foreach (var (id, handle) in Pads)
        {
            var pad = (SDL_Gamepad*)handle;
            GamepadButtons current = 0;
            foreach (var (sdl, flag) in ButtonMap)
            {
                if (SDL_GetGamepadButton(pad, sdl))
                {
                    current |= flag;
                }
            }

            // Fold the left stick into the D-pad directions. SDL's Y axis is
            // positive-down — the opposite of XInput's ThumbLY.
            var lx = SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX);
            var ly = SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY);
            if (ly < -StickDeadzone)
            {
                current |= GamepadButtons.DPadUp;
            }

            if (ly > StickDeadzone)
            {
                current |= GamepadButtons.DPadDown;
            }

            if (lx < -StickDeadzone)
            {
                current |= GamepadButtons.DPadLeft;
            }

            if (lx > StickDeadzone)
            {
                current |= GamepadButtons.DPadRight;
            }

            if (SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER) > TriggerThreshold)
            {
                current |= GamepadButtons.LeftTrigger;
            }
            if (SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER) > TriggerThreshold)
            {
                current |= GamepadButtons.RightTrigger;
            }

            Snapshot.Add(new PadSnapshot((uint)id, current));
        }
        return Snapshot;
    }

    private static void OpenPad(SDL_JoystickID id)
    {
        if (Pads.ContainsKey(id))
        {
            return;
        }
        var pad = SDL_OpenGamepad(id);
        if (pad == null)
        {
            Log.Warn($"SDL_OpenGamepad({id}) failed: {SDL_GetError()}");
            return;
        }
        Pads[id] = (nint)pad;
        var paddles = SDL_GamepadHasButton(pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE1);
        Log.Info($"Gamepad added: '{SDL_GetGamepadName(pad)}' type={SDL_GetGamepadType(pad)} paddles={paddles}");
    }

    private static void ClosePad(SDL_JoystickID id)
    {
        if (!Pads.Remove(id, out var pad))
        {
            return;
        }
        Log.Info($"Gamepad removed: '{SDL_GetGamepadName((SDL_Gamepad*)pad)}'");
        SDL_CloseGamepad((SDL_Gamepad*)pad);
    }
}
