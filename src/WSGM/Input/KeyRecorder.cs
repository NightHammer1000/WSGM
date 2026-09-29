using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Input;

/// <summary>Records a keyboard shortcut by listening to raw key events with a
/// low-level hook, so we capture actual virtual-key codes (what RegisterHotKey wants)
/// instead of guessing them from a UI key enum. The hook lives only while recording.</summary>
public sealed class KeyRecorder : IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const int VkLShift = 0xA0, VkRShift = 0xA1;
    private const int VkLControl = 0xA2, VkRControl = 0xA3;
    private const int VkLMenu = 0xA4, VkRMenu = 0xA5;
    private const int VkLWin = 0x5B, VkRWin = 0x5C;
    private const int VkEscape = 0x1B;

    private static KeyRecorder? _active;
    private nint _hook;

    /// <summary>Fires with the captured shortcut. Escape cancels and reports
    /// <see cref="Cleared"/>.</summary>
    public event Action<HotkeyConfig>? Recorded;

    /// <summary>A cleared shortcut: disabled, no modifiers, no key.</summary>
    /// <remarks>Every field is set explicitly because <see cref="HotkeyConfig"/>'s
    /// defaults describe the shipped Ctrl+Alt+Home shortcut, not an empty one.</remarks>
    public static HotkeyConfig Cleared() => new()
    {
        Enabled = false,
        Ctrl = false,
        Alt = false,
        Shift = false,
        Win = false,
        VirtualKey = 0,
    };

    /// <summary>Installs the low-level keyboard hook and begins capturing one shortcut.</summary>
    public void Start()
    {
        if (_active is { } previous && !ReferenceEquals(previous, this))
        {
            // HookProc dispatches through _active only, so simply repointing it
            // would strand the previous recorder's hook in the system keystroke
            // path with no recording in progress. The hook may exist only for the
            // lifetime of an active recording.
            Log.Warn("Key recorder: a new recording replaced an active one; releasing the previous keyboard hook.");
            previous.Stop();
        }
        Stop();
        _active = this;
        unsafe
        {
            delegate* unmanaged<int, nint, nint, nint> callback = &HookProc;
            _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, (nint)callback, 0, 0);
        }
        if (_hook == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Stop();     // clear _active so the failed recorder isn't statically rooted
            Log.Warn($"Could not install keyboard hook for recording (Win32 error {error}).");
            Recorded?.Invoke(Cleared());
        }
    }

    /// <summary>Stops keyboard capture and removes the low-level hook.</summary>
    public void Stop()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        var recorder = _active;
        if (recorder is null || nCode < 0)
        {
            return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        if (message is not (WmKeyDown or WmSysKeyDown))
        {
            return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
        }

        int vk;
        unsafe
        {
            // KbdLlHookStruct is blittable; read it straight from the hook data.
            vk = (int)((NativeMethods.KbdLlHookStruct*)lParam)->vkCode;
        }

        // Modifier alone isn't a shortcut — keep waiting for the real key.
        if (IsModifier(vk))
        {
            return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
        }

        // Captured directly as the stored configuration shape, so the recorded
        // shortcut is never round-tripped through RegisterHotKey's flag encoding.
        var cancelled = vk == VkEscape;
        HotkeyConfig hotkey = cancelled
            ? Cleared()
            : new HotkeyConfig
            {
                Enabled = true,
                Ctrl = IsDown(VkControl),
                Alt = IsDown(VkMenu),
                Shift = IsDown(VkShift),
                Win = IsDown(VkLWin) || IsDown(VkRWin),
                VirtualKey = vk,
            };

        // Unhook synchronously (LL hooks run on the installing thread, so this is
        // safe here): with the unhook deferred to the posted callback, a second
        // keydown arriving first would fire Recorded again.
        recorder.Stop();
        Dispatcher.UIThread.Post(() => recorder.Recorded?.Invoke(hotkey));

        // Swallow the key so recording doesn't type into the UI behind it.
        return 1;
    }

    private static bool IsModifier(int vk) =>
        vk is VkShift or VkControl or VkMenu
            or VkLShift or VkRShift or VkLControl or VkRControl
            or VkLMenu or VkRMenu or VkLWin or VkRWin;

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Human-readable shortcut text, e.g. "Ctrl + Alt + Home".</summary>
    public static string Describe(HotkeyConfig hotkey)
    {
        if (!hotkey.Enabled || hotkey.VirtualKey == 0)
        {
            return "None";
        }
        var parts = new List<string>();
        if (hotkey.Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (hotkey.Alt)
        {
            parts.Add("Alt");
        }

        if (hotkey.Shift)
        {
            parts.Add("Shift");
        }

        if (hotkey.Win)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(hotkey.VirtualKey));
        return string.Join(" + ", parts);
    }

    /// <summary>Converts a Win32 virtual-key code into its user-facing name.</summary>
    /// <param name="vk">The virtual-key code to describe.</param>
    /// <returns>A readable key name.</returns>
    public static string KeyName(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),                 // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),                 // A-Z
        >= 0x60 and <= 0x69 => $"Numpad {vk - 0x60}",
        0x6A => "Numpad *",
        0x6B => "Numpad +",
        0x6C => "Numpad Separator",
        0x6D => "Numpad -",
        0x6E => "Numpad .",
        0x6F => "Numpad /",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",                       // F1-F24
        0xBA => ";",
        0xBB => "+",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"Key 0x{vk:X2}",
    };

    /// <summary>Stops capture and releases the keyboard hook.</summary>
    public void Dispose() => Stop();
}
