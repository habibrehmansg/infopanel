using System.Reflection;
using System.Runtime.Loader;

namespace InfoPanel.Plugins.Loader
{
    public class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        // Assemblies that must be shared with the host process to enable cross-context
        // interop (e.g. IPluginImageWriter.Bitmap returns SKBitmap — the type must be
        // the same instance in both the host and the plugin).
        private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "InfoPanel.Plugins.Graphics",
            "SkiaSharp",
        };

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null && SharedAssemblies.Contains(assemblyName.Name))
            {
                // Fall back to the default (host) context so types are shared
                return null;
            }

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }
}
