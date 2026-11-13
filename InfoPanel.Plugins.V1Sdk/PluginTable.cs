using System.Data;

namespace InfoPanel.Plugins
{
    public class PluginTable(string id, string name, DataTable value, string defaultFormat) : IPluginTable
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public DataTable Value { get; set; } = value;
        public string DefaultFormat { get; } = defaultFormat;

        public PluginTable(string name, DataTable value, string defaultFormat): this(IdUtil.Encode(name), name, value, defaultFormat)
        {
        }

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
