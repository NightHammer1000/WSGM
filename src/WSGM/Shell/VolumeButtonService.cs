using System;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Owns hardware-volume handling while WSGM is the shell. Explorer
/// remains the owner in desktop mode, avoiding double application of a button
/// press when the normal Windows taskbar is present.</summary>
internal sealed class VolumeButtonService : IDisposable
{
    private readonly MessageWindow _window;
    private readonly VolumeIndicator _indicator;
    private readonly AudioManager _audio;
    private bool _gameModeActive;
    private bool _disposed;

    /// <summary>Creates the game-mode volume handler on the Avalonia UI thread.</summary>
    /// <param name="window">The process message-only window carrying the shell hook.</param>
    /// <param name="uiScale">The current UI scale for the OSD.</param>
    /// <param name="audio">The session's audio state owner, told about every volume
    /// this service writes so the taskbar slider does not lag the OSD by a poll.</param>
    internal VolumeButtonService(MessageWindow window, Func<double> uiScale, AudioManager audio)
    {
        _window = window;
        _indicator = new VolumeIndicator(uiScale);
        _audio = audio;
        _window.ShellHookReceived += OnShellHook;
    }

    /// <summary>Enables or disables WSGM's replacement-shell volume handling.</summary>
    internal void SetGameModeActive(bool active)
    {
        if (_disposed || _gameModeActive == active)
        {
            return;
        }

        _gameModeActive = active;
        if (active)
        {
            // VolumeFeedback is preopened by AudioManager.Start, which the session
            // runs before this service exists; Play() self-initializes as backstop.
            if (_window.RegisterShellHook())
            {
                Log.Info("Game-mode volume buttons enabled (shell hook + default audio endpoint).");
            }
            else
            {
                Log.Warn("Game-mode volume buttons unavailable: shell-hook registration failed.");
            }
            return;
        }

        _indicator.Hide();
        _window.DeregisterShellHook();
        Log.Info("Game-mode volume buttons disabled; Explorer owns volume commands.");
    }

    private void OnShellHook(nint eventCode, nint data)
    {
        if (!_gameModeActive || eventCode != NativeMethods.HshellAppCommand)
        {
            return;
        }

        if (VolumeAppCommands.FromShellHookLParam(data) is not { } command)
        {
            return;
        }

        try
        {
            var result = CoreAudio.ApplyCommand(command, out var percentage, out var muted);
            if (result >= 0)
            {
                Log.Info($"Volume button {command} applied to the default audio endpoint " +
                         $"({percentage}%, muted={muted != 0}).");
                // The write already happened above; hand the landed state to the
                // audio owner so the taskbar slider matches the OSD immediately.
                _audio.NoteExternalVolume(percentage, muted != 0);
                VolumeFeedback.Play();
                if (VolumeOsdVisibility.CanShow())
                {
                    _indicator.Show(percentage, muted != 0);
                }
                else
                {
                    _indicator.Hide();
                }
            }
            else
            {
                Log.Warn($"Volume button {command} failed (HRESULT 0x{result:X8}).");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Volume button {command} failed unexpectedly.", ex);
        }
    }

    /// <summary>Unsubscribes and relinquishes shell-hook ownership.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _window.ShellHookReceived -= OnShellHook;
        _indicator.Dispose();
        if (_gameModeActive)
        {
            _window.DeregisterShellHook();
        }
    }
}
