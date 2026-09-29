using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>
/// The flat C ABI of <c>libviiper</c>, WSGM's virtual-USB controller backend.
/// </summary>
/// <remarks>
/// VIIPER runs its USBIP server in-process behind this ABI, so a virtual controller needs no helper
/// process. Every signature here is blittable, keeping the native ownership boundary small and
/// explicit.
/// <para>
/// The kernel side is <c>usbip-win2</c>'s generic signed driver, installed once by the installer.
/// Nothing in this file installs, repairs, or elevates anything; a missing library or driver simply
/// makes controller management unavailable.
/// </para>
/// </remarks>
internal static partial class NativeViiper
{
    private const string Library = "libviiper";

    /// <summary>Return value of every entry point that succeeded.</summary>
    internal const int Ok = 0;

    [LibraryImport(Library, EntryPoint = "viiper_init", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Init(string listenAddress);

    /// <summary>Stops the server and releases every bus and device it owns.</summary>
    [LibraryImport(Library, EntryPoint = "viiper_shutdown")]
    internal static partial void Shutdown();

    [LibraryImport(Library, EntryPoint = "viiper_bus_create")]
    internal static partial int BusCreate(uint busId);

    [LibraryImport(
        Library,
        EntryPoint = "viiper_device_add",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int DeviceAdd(uint busId, string typeName, out uint deviceId);

    [LibraryImport(Library, EntryPoint = "viiper_device_attach")]
    internal static partial int DeviceAttach(uint busId, uint deviceId);

    [LibraryImport(Library, EntryPoint = "viiper_device_remove")]
    internal static partial int DeviceRemove(uint busId, uint deviceId);

    /// <summary>Opens the lock-free submission handle for a device.</summary>
    /// <remarks>
    /// The fast path exists because the ordinary submission entry point takes the library's global
    /// mutex, which is the wrong cost on a path that runs at the controller's poll rate.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "viiper_device_open_fast")]
    internal static partial int DeviceOpenFast(uint busId, uint deviceId, out uint handle);

    /// <summary>Submits one input frame through the fast path.</summary>
    /// <remarks>The buffer is decoded synchronously and never retained by the library.</remarks>
    [LibraryImport(Library, EntryPoint = "viiper_device_set_input_fast")]
    internal static unsafe partial int DeviceSetInputFast(uint handle, byte* data, int length);

    /// <summary>Registers the host-to-device feedback callback; it runs on a library thread.</summary>
    [LibraryImport(Library, EntryPoint = "viiper_device_set_feedback_callback")]
    internal static unsafe partial int DeviceSetFeedbackCallback(
        uint busId,
        uint deviceId,
        delegate* unmanaged[Cdecl]<uint, uint, byte*, int, void*, void> callback,
        void* userData);

    /// <summary>Returns the last error text, or null; release it with <see cref="FreeString"/>.</summary>
    [LibraryImport(Library, EntryPoint = "viiper_last_error")]
    internal static partial IntPtr LastError();

    [LibraryImport(Library, EntryPoint = "viiper_free_string")]
    internal static partial void FreeString(IntPtr value);

    /// <summary>Reads and releases the library's last error message.</summary>
    /// <returns>The message, or a stable placeholder when the library reported none.</returns>
    internal static string TakeLastError()
    {
        IntPtr text = IntPtr.Zero;
        try
        {
            text = LastError();
            return text == IntPtr.Zero
                ? "The controller backend reported no detail."
                : Marshal.PtrToStringUTF8(text) ?? "The controller backend reported no detail.";
        }
        catch (DllNotFoundException)
        {
            return "The controller backend library is not installed.";
        }
        catch (EntryPointNotFoundException)
        {
            return "The installed controller backend library is the wrong version.";
        }
        finally
        {
            if (text != IntPtr.Zero)
            {
                FreeString(text);
            }
        }
    }
}
