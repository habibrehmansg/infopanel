using System.Reflection;

namespace InfoPanel.Plugins.Loader
{
    public class PluginLoader
    {
        public IEnumerable<IPlugin> InitializePlugin(string pluginPath)
        {
            Assembly pluginAssembly = LoadPlugin(pluginPath);
            return CreateCommands(pluginAssembly);
        }


        static Assembly LoadPlugin(string pluginPath)
        {
            PluginLoadContext loadContext = new(pluginPath);
            return loadContext.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(pluginPath)));
        }

        static IEnumerable<IPlugin> CreateCommands(Assembly assembly)
        {
            int count = 0;

            foreach (Type type in assembly.GetTypes())
            {
                if (typeof(IPlugin).IsAssignableFrom(type))
                {
                    if (Activator.CreateInstance(type) is IPlugin result)
                    {
                        count++;
                        yield return result;
                    }
                }
            }

            if (count == 0)
            {
                string availableTypes = string.Join(",", assembly.GetTypes().Select(t => t.FullName));
                throw new ApplicationException(
                    $"Can't find any type which implements ICommand in {assembly} from {assembly.Location}.\n" +
                    $"Available types: {availableTypes}");
            }
        }
    }
}
