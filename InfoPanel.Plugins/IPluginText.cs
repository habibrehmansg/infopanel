using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Plugins
{
    public interface IPluginText
    {
        string Id { get; }
        string Name { get; }
        string Value { get; set; }
    }
}
