using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Plugins.Loader
{
    public class PluginLoader
    {
       public void test(string folder)
        {
           var plugins= Directory.GetFiles(folder, "InfoPanel.*.dll");
            IEnumerable<IPlugin> commands = plugins.SelectMany(pluginPath =>
            {
                Assembly pluginAssembly = LoadPlugin(pluginPath);
                return CreateCommands(pluginAssembly);
            }).ToList();

            //foreach (var command in commands)
            //{
            //    Trace.WriteLine(command);
            //    var panelDatas = command.GetData();
            //    foreach(var panelData in panelDatas)
            //    {
            //        Trace.WriteLine(panelData.CollectionName);
            //        foreach(var item in panelData.EntryList)
            //        {
            //            Trace.WriteLine($"{item.Name}: {item.Value} {item.Unit}");
            //        }
            //    }
            //}
        }

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
