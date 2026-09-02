using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Finds the home app's main window by process name(s) + window class and
/// brings it to the foreground. Port of AnyFSE's window matching (MIT).</summary>
public static class WindowFinder
{
    private sealed class SearchState
    {
        public required HashSet<uint> ProcessIds;
        public string? WindowClass;
        public nint Found;
    }

    private sealed class ListState
    {
        public required List<AppWindow> Result;
        public uint OwnPid;
        public nint ShellWindow;
        public required HashSet<nint> IncludedOwnWindows;
    }

    // Own-process windows normally never appear in the switcher (overlay, taskbar,
    // tray host, splash — UI chrome). The settings window is the exception: in game
    // mode WSGM hosts the only taskbar, so a settings window that drops behind Big
    // Picture is otherwise unreachable. It opts in here for its lifetime.
    private static readonly object IncludeGate = new();
    private static readonly HashSet<nint> IncludedOwnWindows = [];

    /// <summary>Adds an own-process top-level window to the switchable list despite
    /// the own-process exclusion (the settings window). Safe to call repeatedly.</summary>
    /// <param name="hwnd">The window handle to include; zero is ignored.</param>
    public static void IncludeOwnWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }
        lock (IncludeGate)
        {
            IncludedOwnWindows.Add(hwnd);
        }
    }

    /// <summary>Removes a window previously added by <see cref="IncludeOwnWindow"/>.</summary>
    /// <param name="hwnd">The window handle to stop including.</param>
    public static void ExcludeOwnWindow(nint hwnd)
    {
        lock (IncludeGate)
        {
            IncludedOwnWindows.Remove(hwnd);
        }
    }

    // Names whose session-id query has already been reported once. The callers are
    // polls, so an unthrottled warning per pid per tick would flood the capped log.
    private static readonly HashSet<string> WarnedSessionIdNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This process's session id, read once: a process cannot change
    /// sessions, and the callers are polls that must not leak a Process handle
    /// per query.</summary>
    public static int CurrentSessionId { get; } = ReadCurrentSessionId();

    private static int ReadCurrentSessionId()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }

    /// <summary>Finds process identifiers whose names appear in a semicolon-separated allowlist.</summary>
    /// <param name="semicolonNames">Case-insensitive process names separated by semicolons.</param>
    /// <returns>The matching process identifiers.</returns>
    public static HashSet<uint> FindProcessIds(string semicolonNames)
    {
        var result = new HashSet<uint>();
        // Disposed, and resolved once per process: this runs on the splash's 250 ms
        // Big-Picture poll and the 5 s Steam monitor, so an undisposed Process per
        // call leaked a handle four times a second for the life of the session. Our
        // own session id cannot change while we run.
        var session = CurrentSessionId;
        foreach (var name in semicolonNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var plain = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            foreach (var p in Process.GetProcessesByName(plain))
            {
                // Other sessions (RDP, fast user switching) run their own Steam and
                // startup tools — only this session's processes count.
                try
                {
                    if (p.SessionId == session)
                    {
                        result.Add((uint)p.Id);
                    }
                }
                // Catch EVERYTHING, deliberately, and warn only once per name: this
                // feeds the splash's Big Picture detection poll, where a propagated
                // exception or an unthrottled log line each broke a boot — see
                // docs\boot-and-shell.md invariant 7. Do not narrow it.
                catch (Exception ex)
                {
                    if (WarnedSessionIdNames.Add(plain))
                    {
                        Log.Warn($"Session id unreadable for {plain} (pid {p.Id}): {ex.Message}. "
                            + "Further occurrences for this name are not logged.");
                    }
                }
                finally { p.Dispose(); }
            }
        }
        return result;
    }

    /// <summary>Describes the current foreground window as "0x&lt;hwnd&gt; (process)" for
    /// the log. <c>SendInput</c> has no window target — Windows delivers a synthetic
    /// chord to whatever holds focus — so this is the only way a pasted log can show
    /// where a "shortcut sent" line actually went.</summary>
    /// <returns>A short description, or "none" when there is no foreground window.</returns>
    public static string DescribeForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            return "none";
        }
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return $"0x{hwnd:X} ({process.ProcessName})";
        }
        catch (Exception)
        {
            return $"0x{hwnd:X}";
        }
    }

    /// <summary>Finds the first qualifying top-level window owned by an allowed process.</summary>
    /// <param name="processNames">Semicolon-separated process names that may own the window.</param>
    /// <param name="windowClass">An optional exact Win32 window-class filter.</param>
    /// <returns>The native window handle, or zero when no qualifying window exists.</returns>
    public static unsafe nint FindWindow(string processNames, string? windowClass)
        => FindWindow(FindProcessIds(processNames), windowClass);

    /// <summary>Finds the first qualifying top-level window owned by one of the given processes.</summary>
    /// <param name="processIds">The process ids that may own the window.</param>
    /// <param name="windowClass">An optional exact Win32 window-class filter.</param>
    /// <returns>The native window handle, or zero when no qualifying window exists.</returns>
    public static unsafe nint FindWindow(HashSet<uint> processIds, string? windowClass)
    {
        if (processIds.Count == 0)
        {
            return 0;
        }

        var state = new SearchState { ProcessIds = processIds, WindowClass = string.IsNullOrWhiteSpace(windowClass) ? null : windowClass };
        RunEnumWindows(&EnumWindowsProc, state);
        return state.Found;
    }

    /// <summary>Best-effort check that a process's image path equals the expected full
    /// path. Unreadable processes count as matching (fail-open) so the caller's focus
    /// poll cannot go blind when the image path cannot be queried.</summary>
    /// <param name="pid">The process to inspect.</param>
    /// <param name="expectedFullPath">The full image path required.</param>
    /// <returns>Whether the image path matches (or could not be read).</returns>
    public static bool ProcessImagePathEquals(uint pid, string expectedFullPath)
        => NativeShellProcess.TryGetImagePath(pid) is not { } path
            || string.Equals(path, expectedFullPath, StringComparison.OrdinalIgnoreCase);

    [UnmanagedCallersOnly]
    private static int EnumWindowsProc(nint hWnd, nint lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is not SearchState state)
        {
            return 0;
        }

        if (!NativeMethods.IsWindowVisible(hWnd))
        {
            return 1;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (!state.ProcessIds.Contains(pid))
        {
            return 1;
        }

        if (state.WindowClass is not null)
        {
            var buffer = new char[256];
            var len = NativeMethods.RealGetWindowClassW(hWnd, buffer, (uint)buffer.Length);
            var className = new string(buffer, 0, (int)len);
            if (!string.Equals(className, state.WindowClass, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
        }

        state.Found = hWnd;
        return 0; // stop enumeration
    }

    /// <summary>A visible, switchable top-level window discovered during enumeration.</summary>
    /// <param name="Hwnd">The native window handle.</param>
    /// <param name="Title">The title presented in the switcher.</param>
    /// <param name="ProcessId">The identifier of the owning process.</param>
    public sealed record AppWindow(nint Hwnd, string Title, uint ProcessId)
    {
        /// <summary>Gets whether the window was minimized at enumeration time.</summary>
        public bool IsMinimized { get; init; }
    }

    /// <summary>Alt-tab style enumeration: visible, titled, top-level windows that
    /// are not tool windows, not DWM-cloaked (suspended UWP ghosts), not the shell's
    /// desktop window ("Program Manager"), and not ours. Z-order top first.</summary>
    public static unsafe List<AppWindow> ListSwitchableWindows()
    {
        HashSet<nint> included;
        lock (IncludeGate)
        {
            included = [.. IncludedOwnWindows];
        }
        var state = new ListState
        {
            Result = [],
            OwnPid = (uint)Environment.ProcessId,
            ShellWindow = NativeMethods.GetShellWindow(),
            IncludedOwnWindows = included,
        };
        RunEnumWindows(&ListWindowsProc, state);
        return state.Result;
    }

    /// <summary>The pure alt-tab filter decision, separated from the Win32 queries
    /// that feed it so the specification is unit-testable: a window is switchable
    /// when it is visible, titled, not the shell's desktop window, not a tool
    /// window, not DWM-cloaked, and not owned by this process.</summary>
    /// <param name="isVisible">Whether the window reports WS_VISIBLE (minimized windows still do).</param>
    /// <param name="isShellWindow">Whether the window is the shell's desktop window (Progman).</param>
    /// <param name="exStyle">The window's extended style bits.</param>
    /// <param name="isOwnProcess">Whether this process owns the window.</param>
    /// <param name="cloaked">The DWM cloaked attribute (non-zero for suspended UWP ghosts).</param>
    /// <param name="titleLength">The window title length in characters.</param>
    /// <returns>Whether the window belongs in an alt-tab-style list.</returns>
    public static bool PassesSwitchableFilter(
        bool isVisible, bool isShellWindow, int exStyle, bool isOwnProcess, uint cloaked, int titleLength)
        => isVisible
            && !isShellWindow
            && (exStyle & NativeMethods.WsExToolWindow) == 0
            && !isOwnProcess
            && cloaked == 0
            && titleLength > 0;

    [UnmanagedCallersOnly]
    private static int ListWindowsProc(nint hWnd, nint lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is not ListState state)
        {
            return 0;
        }
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        // Cloak query failure counts as not cloaked.
        var cloaked = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DwmWaCloaked, out var value, 4) == 0
            ? value
            : 0u;
        var buffer = new char[256];
        var length = NativeMethods.GetWindowTextW(hWnd, buffer, buffer.Length);
        // An opted-in own window (the settings window) is treated as not-ours so it
        // still has to clear every other filter (visible, titled, not a tool window).
        var treatAsOwn = pid == state.OwnPid && !state.IncludedOwnWindows.Contains(hWnd);
        // Explorer's Progman is visible, plain-styled, and titled "Program
        // Manager", yet real Alt-Tab never offers it.
        if (!PassesSwitchableFilter(
                NativeMethods.IsWindowVisible(hWnd),
                hWnd == state.ShellWindow,
                NativeMethods.GetWindowLong(hWnd, NativeMethods.GwlExStyle),
                treatAsOwn,
                cloaked,
                length))
        {
            return 1;
        }
        state.Result.Add(new AppWindow(hWnd, new string(buffer, 0, length), pid)
        {
            IsMinimized = NativeMethods.IsIconic(hWnd),
        });
        return 1;
    }

    /// <summary>UnmanagedCallersOnly callbacks cannot capture state, so it travels
    /// through EnumWindows' lParam as a GCHandle — one pattern for both callbacks,
    /// no shared statics, no lock.</summary>
    private static unsafe void RunEnumWindows(delegate* unmanaged<nint, nint, int> callback, object state)
    {
        var handle = GCHandle.Alloc(state);
        try
        {
            NativeMethods.EnumWindows((nint)callback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Best-effort focus. Against an elevated window SetForegroundWindow may
    /// fail silently under UIPI — callers should prefer protocol re-activation.</summary>
    public static void BringToForeground(nint hWnd)
    {
        if (hWnd == 0)
        {
            return;
        }
        // SW_RESTORE on a MAXIMIZED window would drop it back to normal size —
        // only a minimized window needs restoring before it can take foreground.
        if (NativeMethods.IsIconic(hWnd))
        {
            NativeMethods.ShowWindow(hWnd, NativeMethods.SwRestore);
        }
        NativeMethods.SetForegroundWindow(hWnd);
    }
}
