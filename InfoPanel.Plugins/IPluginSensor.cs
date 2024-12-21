using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Plugins
{
    public interface IPluginSensor: IPluginData
    {
        float Value { get; set; }
        string? Unit { get; }
    }
}
