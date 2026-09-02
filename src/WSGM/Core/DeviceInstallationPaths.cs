using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Administrator-protected locations owned by the WSGM installer.</summary>
internal static class DeviceInstallationPaths
{
    internal static string ProtectedRoot
    {
        get
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                throw new DirectoryNotFoundException(
                    "Windows did not report the protected Program Files directory.");
            }

            return Path.Combine(programFiles, "WSGM");
        }
    }

    internal static string InstalledPackageRoot =>
        Path.Combine(ProtectedRoot, "DevicePlugins", "installed");
}
