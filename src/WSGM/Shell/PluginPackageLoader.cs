using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using WSGM.Core;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Shell;

/// <summary>Loads the sole validated device plugin into a package-local collectible context.</summary>
internal sealed class PluginPackageLoader : IDisposable
{
    private readonly PluginLoadContext _loadContext;
    private bool _disposed;

    private PluginPackageLoader(
        string packageRoot,
        PluginLoadContext loadContext,
        IDevicePlugin plugin)
    {
        PackageRoot = packageRoot;
        _loadContext = loadContext;
        Plugin = plugin;
    }

    internal string PackageRoot { get; }

    internal IDevicePlugin Plugin { get; }

    internal static PluginPackageLoader Load(InstalledDevicePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.Valid || package.Manifest is null)
        {
            throw new InvalidDataException("The installed device package is not valid.");
        }

        string root = Path.GetFullPath(package.PackagePath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The plugin package directory is missing.");
        }

        string entryPath = ConstrainPackagePath(root, package.Manifest.EntryAssembly);
        if (!File.Exists(entryPath))
        {
            throw new FileNotFoundException("The plugin entry point is missing.", entryPath);
        }

        PluginLoadContext context = new(root, entryPath);
        IDevicePlugin? plugin = null;
        try
        {
            Assembly assembly;
            using (FileStream entry = File.OpenRead(entryPath))
            {
                // Loading the entry image from a stream avoids pinning the installed DLL for the
                // lifetime of the collectible context. The plugin can therefore be replaced as
                // soon as its lifecycle is quiescent; dependencies still resolve package-locally.
                assembly = context.LoadFromStream(entry);
            }
            Type entryType = assembly.GetType(
                package.Manifest.EntryType,
                throwOnError: false,
                ignoreCase: false)
                ?? throw new InvalidDataException("The declared plugin entry type was not found.");
            if (!entryType.IsPublic
                || entryType.IsAbstract
                || entryType.IsInterface
                || entryType.ContainsGenericParameters
                || !typeof(IDevicePlugin).IsAssignableFrom(entryType))
            {
                throw new InvalidDataException(
                    "The declared entry type must be a public, concrete, non-generic IDevicePlugin.");
            }

            if (entryType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidDataException(
                    "The plugin entry type needs a public parameterless constructor.");
            }

            plugin = Activator.CreateInstance(entryType) as IDevicePlugin
                ?? throw new InvalidDataException(
                    "The plugin entry type did not create an IDevicePlugin instance.");
            if (!string.Equals(plugin.PackageId, package.Manifest.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The plugin code and manifest package identifiers differ.");
            }

            return new PluginPackageLoader(root, context, plugin);
        }
        catch (Exception loadFailure)
        {
            List<Exception> failures = [loadFailure];
            if (plugin is not null)
            {
                try
                {
                    plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception disposalFailure)
                {
                    failures.Add(disposalFailure);
                }
            }

            try
            {
                context.Unload();
            }
            catch (Exception unloadFailure)
            {
                failures.Add(unloadFailure);
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(loadFailure).Throw();
            }

            throw new AggregateException(
                "Plugin loading and resource cleanup were not both verified.",
                failures);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadContext.Unload();
    }

    private static string ConstrainPackagePath(string packageRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Package paths must be non-empty and relative.");
        }

        string rootPrefix = packageRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A package path escaped the package directory.");
        }

        return candidate;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly string SdkName = typeof(IDevicePlugin).Assembly.GetName().Name!;
        private readonly string _packageRoot;
        private readonly AssemblyDependencyResolver _resolver;

        internal PluginLoadContext(string packageRoot, string entryPath)
            : base($"WSGM.Plugin:{Path.GetFileName(packageRoot)}", isCollectible: true)
        {
            _packageRoot = packageRoot;
            _resolver = new AssemblyDependencyResolver(entryPath);
        }

        // Assemblies that may exist only once per process, whatever version the package carries.
        // CsWinRT's runtime registers a process-global ComWrappers instance when it first runs; a
        // second copy loaded into this context makes whichever side initializes second throw
        // "Attempt to update previously set global instance" for the rest of the process. The
        // Claw package ships both (any `-windows10.0.x` plugin build copies them), and the plugin
        // touched WinRT first, so WSGM's own Wi-Fi and Bluetooth queries were the side that died
        // (device-reproduced 2026-09-01).
        private static readonly Dictionary<string, Assembly> HostOwned = new(StringComparer.Ordinal)
        {
            [SdkName] = typeof(IDevicePlugin).Assembly,
            [typeof(WinRT.IWinRTObject).Assembly.GetName().Name!] = typeof(WinRT.IWinRTObject).Assembly,
            [typeof(Windows.Foundation.Point).Assembly.GetName().Name!] =
                typeof(Windows.Foundation.Point).Assembly,
        };

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The host's SDK is the type-identity boundary, whatever assembly version the plugin
            // was compiled against. Deferring to the default context instead would re-check the
            // version and refuse a plugin built against a different SDK build even when the
            // contract still matches - the manifest apiVersion is the real compatibility gate,
            // not the assembly version. The WinRT pair is pinned for the same reason plus the
            // process-global registration above.
            if (HostOwned.TryGetValue(assemblyName.Name ?? string.Empty, out Assembly? hostOwned))
            {
                return hostOwned;
            }

            // Host-first for everything else: a dependency the host already ships is shared, not
            // duplicated. Package authors cannot be expected to trim the framework and runtime
            // assemblies their SDK copies beside the plugin, and a second copy of anything that
            // holds process-wide state (native handles, COM registrations, static caches) is a
            // fault the host cannot recover from. The package-local copy is only used for
            // assemblies the host does not have at all — which is the isolation the context is
            // for. A version the host cannot satisfy also falls through to the package copy, so
            // a plugin carrying a newer library than WSGM still loads; that duplicate is logged
            // once because it is the case that can bite later.
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (FileNotFoundException)
            {
                // Not a host assembly: package-local or absent.
            }
            catch (FileLoadException ex)
            {
                if (path is not null)
                {
                    Log.Warn($"Plugin dependency {assemblyName.Name} {assemblyName.Version} loads "
                        + $"from the package because the host's copy does not satisfy it ({ex.Message}).");
                }
            }

            if (path is null)
            {
                return null;
            }

            EnsurePackagePath(path);
            return LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null)
            {
                return nint.Zero;
            }

            EnsurePackagePath(path);
            return LoadUnmanagedDllFromPath(path);
        }

        private void EnsurePackagePath(string path)
        {
            string rootPrefix = _packageRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A resolved dependency escaped the package directory.");
            }
        }
    }
}
