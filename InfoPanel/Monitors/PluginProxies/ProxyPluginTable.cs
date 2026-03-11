using System.Data;
using InfoPanel.Plugins;

namespace InfoPanel.Monitors.PluginProxies
{
    /// <summary>
    /// Data-holder proxy for IPluginTable that receives values via IPC.
    /// </summary>
    internal class ProxyPluginTable(string id, string name, string defaultFormat) : IPluginTable
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public DataTable Value { get; set; } = new();
        public string DefaultFormat { get; } = defaultFormat;

        public override string ToString()
        {
            if (Value.Rows.Count > 0)
            {
                var values = new string[Value.Columns.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    if (Value.Rows[0][i] is IPluginText textColumn)
                    {
                        values[i] = textColumn.Value;
                    }
                    else if (Value.Rows[0][i] is IPluginSensor sensorColumn)
                    {
                        values[i] = $"{sensorColumn}";
                    }
                }
                return string.Join(", ", values);
            }

            return "-";
        }
    }
}
