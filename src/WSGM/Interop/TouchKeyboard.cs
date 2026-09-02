using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Shows or hides Windows' touch keyboard for the current foreground window.</summary>
/// <remarks>
/// Starting <c>TabTip.exe</c> does not toggle an already-running keyboard. Windows' own
/// <c>ITipInvocation</c> object is the shell-facing operation used by hardware keyboard buttons;
/// it is now called directly because the main process is a normal managed Windows application.
/// </remarks>
internal static class TouchKeyboard
{
    /// <summary>Toggles the keyboard and reports whether Windows accepted the request.</summary>
    internal static bool Toggle()
    {
        ITipInvocation? invocation = null;
        object? instance = null;
        try
        {
            Type type = Type.GetTypeFromCLSID(
                new Guid("4CE576FA-83DC-4F88-951C-9D0782B4E376"),
                throwOnError: true)!;
            instance = Activator.CreateInstance(type)
                ?? throw new COMException("Windows did not create the touch-keyboard service.");
            invocation = (ITipInvocation)instance;
            invocation.Toggle(NativeMethods.GetForegroundWindow());
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            WSGM.Core.Log.Warn($"Touch keyboard toggle failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _ = Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    [ComImport]
    [Guid("37C994E7-432B-4834-A2F7-DCE1F13B834B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(nint window);
    }
}
