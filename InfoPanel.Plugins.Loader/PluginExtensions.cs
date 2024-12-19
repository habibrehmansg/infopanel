using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Plugins.Loader
{
    public static class PluginExtensions
    {
        public static string Id(this IPlugin panelData)
        {
            return panelData.ToString();
        }
    }
}
