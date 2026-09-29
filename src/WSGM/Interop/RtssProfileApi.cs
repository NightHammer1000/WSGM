using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Exact dynamically loaded RTSS profile API with no static DLL search.</summary>
internal sealed unsafe partial class RtssProfileApi : IDisposable
{
    private const uint LoadLibrarySearchDllLoadDirectory = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private nint _module;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _loadProfile;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _saveProfile;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, int> _getProfileProperty;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, int> _setProfileProperty;
    private readonly delegate* unmanaged[Cdecl]<void> _updateProfiles;

    internal RtssProfileApi(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        _module = LoadLibraryEx(
            libraryPath,
            0,
            LoadLibrarySearchDllLoadDirectory | LoadLibrarySearchSystem32);
        if (_module == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The verified RTSS profile API could not be loaded.");
        }
        try
        {
            _loadProfile = (delegate* unmanaged[Cdecl]<nint, void>)GetExport("LoadProfile");
            _saveProfile = (delegate* unmanaged[Cdecl]<nint, void>)GetExport("SaveProfile");
            _getProfileProperty =
                (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)GetExport("GetProfileProperty");
            _setProfileProperty =
                (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)GetExport("SetProfileProperty");
            _updateProfiles = (delegate* unmanaged[Cdecl]<void>)GetExport("UpdateProfiles");
        }
        catch
        {
            FreeLibrary(_module);
            _module = 0;
            throw;
        }
    }

    internal void LoadProfile(string profile) => InvokeString(_loadProfile, profile);

    internal void SaveProfile(string profile) => InvokeString(_saveProfile, profile);

    internal bool TryGetUInt32(string property, out uint value)
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        nint propertyPointer = Marshal.StringToCoTaskMemAnsi(property);
        try
        {
            uint readValue = 0;
            bool succeeded = _getProfileProperty(
                propertyPointer,
                (nint)(&readValue),
                sizeof(uint)) != 0;
            value = readValue;
            return succeeded;
        }
        finally
        {
            Marshal.FreeCoTaskMem(propertyPointer);
        }
    }

    internal bool TrySetUInt32(string property, uint value)
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        nint propertyPointer = Marshal.StringToCoTaskMemAnsi(property);
        try
        {
            return _setProfileProperty(propertyPointer, (nint)(&value), sizeof(uint)) != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(propertyPointer);
        }
    }

    internal void UpdateProfiles()
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        _updateProfiles();
    }

    public void Dispose()
    {
        if (_module == 0)
        {
            return;
        }

        FreeLibrary(_module);
        _module = 0;
    }

    private nint GetExport(string name)
    {
        nint address = GetProcAddress(_module, name);
        if (address == 0)
        {
            throw new EntryPointNotFoundException($"RTSS profile API export is absent: {name}.");
        }

        return address;
    }

    private void InvokeString(delegate* unmanaged[Cdecl]<nint, void> function, string value)
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        nint pointer = Marshal.StringToCoTaskMemAnsi(value);
        try
        {
            function(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);
}
