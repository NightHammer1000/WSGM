using System;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Plays the shared, rate-limited and non-backlogging volume preview
/// sound used by both hardware buttons and the taskbar slider.</summary>
internal static class VolumeFeedback
{
    private const long MinimumIntervalMs = 90;
    private static readonly object PlayerGate = new();
    private static long _lastRequestedAt;
    private static int _initializationState;
    private static int _reinitializeRequested;
    private static int _reinitializeWorkerRunning;
    private static WaveOutFeedback? _player;

    /// <summary>Preopens the playback stream away from the UI thread, so the
    /// first volume input never pays the device-open latency.</summary>
    internal static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _initializationState, 1, 0) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _reinitializeRequested, 1);
        StartReinitializeWorker();
    }

    /// <summary>Reopens the mapped playback stream after the system default
    /// output changes. Requests coalesce, but one arriving during an open is
    /// retained so the final stream always follows the newest default.</summary>
    internal static void Reinitialize()
    {
        Interlocked.Exchange(ref _reinitializeRequested, 1);
        Interlocked.Exchange(ref _initializationState, 1);
        StartReinitializeWorker();
    }

    private static void StartReinitializeWorker()
    {
        if (Interlocked.CompareExchange(ref _reinitializeWorkerRunning, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            while (Interlocked.Exchange(ref _reinitializeRequested, 0) != 0)
            {
                InitializeCore();
            }
            Interlocked.Exchange(ref _reinitializeWorkerRunning, 0);
            if (Volatile.Read(ref _reinitializeRequested) != 0)
            {
                StartReinitializeWorker();
            }
        });
    }

    private static void InitializeCore()
    {
        var result = WaveOutFeedback.Open(out var replacement);
        if (result < 0 || replacement is null)
        {
            Log.Warn($"Volume feedback initialization failed (HRESULT 0x{result:X8}).");
            Interlocked.Exchange(ref _initializationState, 0);
            return;
        }

        lock (PlayerGate)
        {
            var previous = _player;
            _player = replacement;
            previous?.Dispose();
        }
        Interlocked.Exchange(ref _initializationState, 2);
    }

    /// <summary>Requests one soft feedback sound. Calls are paced to the cue
    /// length and overlap is dropped, so held controls cannot build a delayed
    /// playback queue.</summary>
    internal static void Play()
    {
        Initialize();
        var now = Environment.TickCount64;
        while (true)
        {
            var previous = Volatile.Read(ref _lastRequestedAt);
            if (now - previous < MinimumIntervalMs)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref _lastRequestedAt, now, previous) == previous)
            {
                break;
            }
        }

        if (!Monitor.TryEnter(PlayerGate))
        {
            return;
        }
        try
        {
            var result = _player?.Play() ?? 1;
            if (result < 0)
            {
                Log.Warn($"Volume feedback sound failed (HRESULT 0x{result:X8}).");
                _player?.Dispose();
                _player = null;
                Interlocked.Exchange(ref _initializationState, 0);
            }
        }
        finally
        {
            Monitor.Exit(PlayerGate);
        }
    }
}
