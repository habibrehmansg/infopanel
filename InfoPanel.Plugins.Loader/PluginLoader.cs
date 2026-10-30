using System.IO;
using System.Reflection;
using IniParser;
using IniParser.Model;

namespace InfoPanel.Plugins.Loader
{
    public class PluginLoader
    {
        public static PluginInfo? GetPluginInfo(string folder)
        {
            var pluginInfo = Path.Combine(folder, "PluginInfo.ini");
            if(File.Exists(pluginInfo))
            {
                var parser = new FileIniDataParser();
                var config = parser.ReadFile(pluginInfo);

                return new PluginInfo
                {
                    Name = config["PluginInfo"]["Name"],
                    Description = config["PluginInfo"]["Description"],
                    Author = config["PluginInfo"]["Author"],
                    Version = config["PluginInfo"]["Version"],
                    Website = config["PluginInfo"]["Website"]
                };

            }

            return null;
        }

        /// <summary>
        /// Loads the DLL into a load context of its own and creates every plugin type in it, except a type marked
        /// <c>[SupportedOSPlatform]</c> only for other operating systems (a plugin built on a Windows-only library, say),
        /// which is listed in <see cref="LoadedPlugins.Skipped"/> instead, so it can't fail the DLL's other plugins. A DLL
        /// marked for another OS as a whole (a <c>net8.0-windows</c> build) is still tried: most use portable APIs.
        /// </summary>
        /// <exception cref="ApplicationException">The DLL has no plugin type at all.</exception>
        public static LoadedPlugins Load(string pluginPath)
        {
            var assembly = LoadPlugin(pluginPath);
            var plugins = new List<IPlugin>();
            var skipped = new List<SkippedPluginType>();
            var found = 0;

            foreach (var type in assembly.GetTypes())
            {
                if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;

                found++;
                var platforms = SupportedPlatforms(type);
                if (platforms.Count > 0 && !platforms.Any(IsThisPlatform))
                {
                    skipped.Add(new SkippedPluginType(type.FullName ?? type.Name, platforms));
                    continue;
                }

                if (Activator.CreateInstance(type) is IPlugin plugin)
                    plugins.Add(plugin);
            }

            if (found == 0)
            {
                string availableTypes = string.Join(",", assembly.GetTypes().Select(t => t.FullName));
                throw new ApplicationException(
                    $"Can't find any type which implements IPlugin in {assembly} from {assembly.Location}.\n" +
                    $"Available types: {availableTypes}");
            }

            return new LoadedPlugins(plugins, skipped);
        }

        /// <summary>The plugins <see cref="Load"/> creates.</summary>
        public static IEnumerable<IPlugin> InitializePlugin(string pluginPath) => Load(pluginPath).Plugins;

        /// <summary>
        /// The platforms a type's <c>[SupportedOSPlatform]</c> attributes name (empty: any). Read by the attribute's
        /// name, so it needn't be the host's type.
        /// </summary>
        internal static IReadOnlyList<string> SupportedPlatforms(Type type) =>
            type.GetCustomAttributesData()
                .Where(a => a.AttributeType.FullName == "System.Runtime.Versioning.SupportedOSPlatformAttribute")
                .Select(a => a.ConstructorArguments.Count == 1 ? a.ConstructorArguments[0].Value as string : null)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();

        /// <summary>Whether a platform name (<c>windows</c>, <c>windows10.0.19041</c>, <c>macos</c>, <c>linux</c>) is this OS, its version ignored.</summary>
        internal static bool IsThisPlatform(string platform)
        {
            var name = new string(platform.TakeWhile(char.IsLetter).ToArray());
            return name.Length > 0 && OperatingSystem.IsOSPlatform(name);
        }

        static Assembly LoadPlugin(string pluginPath)
        {
            PluginLoadContext loadContext = new(pluginPath);
            return loadContext.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(pluginPath)));
        }
    }

    /// <summary>A plugin type the loader didn't create because it is marked for other operating systems.</summary>
    public sealed record SkippedPluginType(string TypeName, IReadOnlyList<string> Platforms);

    /// <summary>What <see cref="PluginLoader.Load"/> found in a DLL.</summary>
    public sealed record LoadedPlugins(IReadOnlyList<IPlugin> Plugins, IReadOnlyList<SkippedPluginType> Skipped);
}
