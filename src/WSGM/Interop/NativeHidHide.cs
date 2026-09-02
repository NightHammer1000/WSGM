using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Interop;

internal static partial class NativeHidHide
{
    private const string ControlDevice = "\\\\.\\HidHide";
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const int InitialBufferBytes = 4096;
    private const int MaximumBufferBytes = 1024 * 1024;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;

    // These values are the CTL_CODE values published by HidHide's FilterDriverProxy.
    internal const uint GetApplications = 0x80016000;
    internal const uint SetApplications = 0x80016004;
    internal const uint GetDevices = 0x80016008;
    internal const uint SetDevices = 0x8001600C;
    internal const uint GetActive = 0x80016010;
    internal const uint GetInverse = 0x80016018;

    internal static bool TryOpen(out SafeFileHandle handle, out int error)
    {
        handle = CreateFileW(
            ControlDevice,
            GenericRead,
            ShareReadWriteDelete,
            0,
            OpenExisting,
            0,
            0);
        if (!handle.IsInvalid)
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return false;
    }

    internal static unsafe bool TryReadBoolean(
        SafeFileHandle handle,
        uint controlCode,
        out bool value,
        out int error)
    {
        byte raw = 0;
        uint returned;
        bool success = DeviceIoControl(
            handle,
            controlCode,
            null,
            0,
            &raw,
            1,
            out returned,
            0);
        if (!success || returned != 1)
        {
            value = false;
            error = success ? 13 : Marshal.GetLastPInvokeError();
            return false;
        }

        value = raw != 0;
        error = 0;
        return true;
    }

    internal static unsafe bool TryReadMultiString(
        SafeFileHandle handle,
        uint controlCode,
        out IReadOnlyList<string> values,
        out int error)
    {
        for (int size = InitialBufferBytes; size <= MaximumBufferBytes; size *= 2)
        {
            byte[] buffer = new byte[size];
            uint returned;
            bool success;
            fixed (byte* output = buffer)
            {
                success = DeviceIoControl(
                    handle,
                    controlCode,
                    null,
                    0,
                    output,
                    (uint)buffer.Length,
                    out returned,
                    0);
            }

            if (success)
            {
                if (returned > (uint)buffer.Length || (returned & 1) != 0)
                {
                    values = [];
                    error = 13;
                    return false;
                }

                return TryDecodeMultiString(buffer.AsSpan(0, (int)returned), out values, out error);
            }

            error = Marshal.GetLastPInvokeError();
            if (error is not ErrorInsufficientBuffer and not ErrorMoreData)
            {
                values = [];
                return false;
            }
        }

        values = [];
        error = ErrorMoreData;
        return false;
    }

    internal static unsafe bool TryWriteMultiString(
        SafeFileHandle handle,
        uint controlCode,
        IReadOnlyList<string> values,
        out int error)
    {
        ArgumentNullException.ThrowIfNull(values);
        byte[] buffer = EncodeMultiString(values);
        uint returned;
        bool success;
        fixed (byte* input = buffer)
        {
            success = DeviceIoControl(
                handle,
                controlCode,
                input,
                (uint)buffer.Length,
                null,
                0,
                out returned,
                0);
        }

        error = success ? 0 : Marshal.GetLastPInvokeError();
        return success;
    }

    private static bool TryDecodeMultiString(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<string> values,
        out int error)
    {
        if (bytes.Length == 0)
        {
            values = [];
            error = 0;
            return true;
        }

        string text = Encoding.Unicode.GetString(bytes);
        List<string> result = [];
        int start = 0;
        while (start < text.Length)
        {
            int terminator = text.IndexOf('\0', start);
            if (terminator < 0)
            {
                values = [];
                error = 13;
                return false;
            }

            if (terminator == start)
            {
                values = result;
                error = 0;
                return true;
            }

            result.Add(text[start..terminator]);
            start = terminator + 1;
        }

        values = [];
        error = 13;
        return false;
    }

    private static byte[] EncodeMultiString(IReadOnlyList<string> values)
    {
        StringBuilder builder = new();
        foreach (string value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Contains('\0'))
            {
                throw new ArgumentException("HidHide entries cannot contain NUL characters.",
                    nameof(values));
            }

            builder.Append(value);
            builder.Append('\0');
        }

        builder.Append('\0');
        if (values.Count == 0)
        {
            builder.Append('\0');
        }

        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        void* inputBuffer,
        uint inputBufferSize,
        void* outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
