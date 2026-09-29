using System;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>
/// Reports which application is in the foreground, so per-application policy can follow the user
/// rather than only what Steam says is running.
/// </summary>
/// <remarks>
/// A WinEvent hook plus a slow poll, because neither alone is enough: the hook is what makes a
/// switch immediate, and the poll is what covers the switches a hook misses — it does not fire for
/// a window that gains focus while the desktop is locked, during some elevation transitions, or if
/// the hook is silently torn down. HandheldCompanion pairs them for the same reason.
/// <para>
/// The callback does the least possible work: it records the window handle and signals. Resolving
/// the process opens a handle and reads a path, which must not happen inside a system-installed
/// hook callback.
/// </para>
/// </remarks>
internal sealed unsafe partial class ForegroundWindowWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private readonly object _gate = new();
    private readonly WinEventProc _callback;
    private readonly System.Threading.Timer _poll;
    private nint _hook;
    private nint _lastWindow;
    private nint _pendingWindow;
    private string _current = string.Empty;
    private int _evaluationQueued;
    private bool _disposed;

    /// <summary>Creates the watcher and begins observing.</summary>
    /// <remarks>
    /// The poll interval is deliberately slow. The hook carries every ordinary switch, so this only
    /// has to notice the ones it missed, and a fast poll would read a process path several times a
    /// second for a value that changes when the user alt-tabs.
    /// </remarks>
    internal ForegroundWindowWatcher()
    {
        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            _callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        if (_hook == 0)
        {
            Log.Warn("Foreground watcher: WinEvent hook not installed; polling only.");
        }

        _poll = new System.Threading.Timer(_ => Evaluate(), null, TimeSpan.Zero, PollInterval);
    }

    /// <summary>
    /// Raised when the foreground application changes, with its executable name and, when the
    /// process was readable, its full image path.
    /// </summary>
    /// <remarks>
    /// Only for a window classified as an application. A restricted foreground leaves the last
    /// application in force, so no event is raised and the running game keeps its profile.
    /// </remarks>
    internal event Action<string, string?>? ApplicationChanged;

    /// <summary>How often the safety-net poll runs.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _poll.Dispose();
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint thread,
        uint time)
    {
        // Nothing but a signal: resolving the process from inside a system hook callback would run
        // a handle open and a path read on whatever thread Windows delivered the event on.
        if (eventType == EventSystemForeground && window != 0)
        {
            Interlocked.Exchange(ref _pendingWindow, window);
            if (Interlocked.Exchange(ref _evaluationQueued, 1) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static watcher => watcher.DrainWinEvents(),
                    this,
                    preferLocal: false);
            }
        }
    }

    private void DrainWinEvents()
    {
        while (true)
        {
            nint window = Interlocked.Exchange(ref _pendingWindow, 0);
            if (window != 0)
            {
                try
                {
                    Evaluate(window);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Foreground watcher evaluation failed: {ex.Message}");
                }
            }

            Interlocked.Exchange(ref _evaluationQueued, 0);
            if (Volatile.Read(ref _pendingWindow) == 0
                || Interlocked.Exchange(ref _evaluationQueued, 1) != 0)
            {
                return;
            }
        }
    }

    private void Evaluate(nint window = 0)
    {
        if (window == 0)
        {
            window = NativeMethods.GetForegroundWindow();
        }

        if (window == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || window == _lastWindow)
            {
                return;
            }

            _lastWindow = window;
        }

        (string executable, string? imagePath) = ResolveExecutable(window);
        if (ForegroundApplicationFilter.Classify(executable)
            is not ForegroundApplicationKind.Application)
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(_current, executable, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _current = executable;
        }

        Log.Info($"Foreground application: {executable}.");
        ApplicationChanged?.Invoke(executable, imagePath);
    }

    /// <remarks>
    /// A UWP application's visible window belongs to the shared host, so the real process is found
    /// by looking for a child window owned by a different one. HandheldCompanion reads it through
    /// WinRT's process diagnostics; that is COM, which this executable cannot use, and the child
    /// walk needs no new dependency. Without it every UWP application reports as the host and they
    /// would all share one profile.
    /// </remarks>
    private static (string Name, string? Path) ResolveExecutable(nint window)
    {
        _ = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return (string.Empty, null);
        }

        if (string.Equals(
                ClassName(window),
                ForegroundApplicationFilter.UwpHostWindowClass,
                StringComparison.Ordinal))
        {
            uint hosted = FindHostedProcess(window, processId);
            if (hosted != 0)
            {
                processId = hosted;
            }
        }

        return ExecutableIdentity(processId);
    }

    private static uint FindHostedProcess(nint window, uint hostProcessId)
    {
        uint found = 0U;
        EnumChildWindows(
            window,
            (child, parameter) =>
            {
                _ = NativeMethods.GetWindowThreadProcessId(child, out uint childProcessId);
                if (childProcessId != 0 && childProcessId != hostProcessId)
                {
                    found = childProcessId;

                    // Stop at the first child owned by another process: that is the hosted
                    // application, and continuing would only find its own child windows.
                    return false;
                }

                return true;
            },
            0);
        return found;
    }

    private static string ClassName(nint window)
    {
        Span<char> buffer = stackalloc char[256];
        fixed (char* pointer = buffer)
        {
            int length = GetClassNameW(window, pointer, buffer.Length);
            return length > 0 ? new string(pointer, 0, length) : string.Empty;
        }
    }

    // An unreadable process is ordinary for an elevated or protected target; the filter treats an
    // empty name as restricted, so an unreadable foreground keeps the previous application.
    private static (string Name, string? Path) ExecutableIdentity(uint processId)
        => NativeShellProcess.TryGetImagePath(processId) is { } path
            ? (System.IO.Path.GetFileName(path), path)
            : (string.Empty, null);

    private delegate void WinEventProc(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint thread,
        uint time);

    private delegate bool EnumChildProc(nint window, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameW(nint window, char* className, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(nint parent, EnumChildProc callback, nint parameter);
}
