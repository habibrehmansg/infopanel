using InfoPanel.Plugins.Ipc;

namespace InfoPanel.Plugins.Host
{
    /// <summary>
    /// Upper bounds on the bytes a value comes to in a host's message, as the host's JSON-RPC formatter writes it (UTF-8):
    /// a string's every character past ASCII, and every control character, counted as a six-byte escape; numbers at their
    /// longest; and each object's member names and punctuation (null members included) at a fixed allowance.
    /// </summary>
    internal static class JsonSize
    {
        // {"Value":n,"ValueMin":n,"ValueMax":n,"ValueAvg":n,"Unit":} is 54 bytes and four numbers of at most 32.
        private const long SensorValueFixed = 192;
        // {"Type":,"TextValue":,"SensorValue":,"SensorName":,"SensorUnit":} is 65 bytes.
        private const long CellFixed = 128;
        // {"Columns":[],"Rows":[],"DefaultFormat":} is 41 bytes.
        private const long TableFixed = 64;
        // {"ContainerId":,"EntryId":,"Type":,"SensorValue":,"TextValue":,"TableValue":,"ImageValue":} is 91 bytes ({"Id":,"Name":,…}
        // less). A picture's base64 isn't counted here: a picture goes alone, sized by its own (IpcLimits.ImageWireBytes).
        private const long EntryFixed = 128;

        // {"Id":,"Name":,"Description":,"ConfigFilePath":,"UpdateIntervalMs":,"Actions":[],"IsConfigurable":false,
        // "ConfigProperties":[],"ImageDescriptors":[]} is 148 bytes, and a number of at most 32.
        private const long MetadataFixed = 256;
        // {"MethodName":,"DisplayName":} is 30 bytes.
        private const long ActionFixed = 64;
        // {"Key":,"DisplayName":,"Description":,"Type":,"Value":,"MinValue":,"MaxValue":,"Step":,"Options":[]} is 100
        // bytes, and three numbers of at most 32 (a value that isn't a string is counted by its text and one more).
        private const long SettingFixed = 256;
        // {"Id":,"Name":,"Width":,"Height":,"MmfName":,"BufferSize":} is 59 bytes, and four numbers of at most 32.
        private const long ImageFixed = 256;
        // A number at its longest.
        private const long Number = 32;

        /// <summary>A container without its entries, {"Id":,"Name":,"IsEphemeralPath":false,"Entries":[]} (52 bytes), and a comma.</summary>
        public const long ContainerFixed = 64;

        /// <summary>{"PluginId":,"Updates":[]} and a comma.</summary>
        public const long BatchFixed = 64;

        /// <summary>A message's JSON-RPC envelope: {"jsonrpc":"2.0","method":"OnSensorUpdate","params":[[]]} and change.</summary>
        public const long MessageFixed = 256;

        public static long Of(string? value)
        {
            if (value == null) return 4;
            long bytes = 2;
            foreach (var c in value)
                bytes += c < 0x20 || c >= 0x80 ? 6 : c is '"' or '\\' ? 2 : 1;
            return bytes;
        }

        public static long Of(SensorValueDto? value) => value == null ? 4 : SensorValueFixed + Of(value.Unit);

        public static long Of(TableCellDto cell) =>
            CellFixed + Of(cell.Type) + Of(cell.TextValue) + Of(cell.SensorValue) + Of(cell.SensorName) + Of(cell.SensorUnit);

        /// <summary>A row and the comma after it.</summary>
        public static long Of(List<TableCellDto> row)
        {
            long bytes = 3;
            foreach (var cell in row)
                bytes += Of(cell) + 1;
            return bytes;
        }

        /// <summary>A table with its columns and no rows (each row adds <see cref="Of(List{TableCellDto})"/>).</summary>
        public static long Header(TableValueDto table)
        {
            var bytes = TableFixed + Of(table.DefaultFormat);
            foreach (var column in table.Columns)
                bytes += Of(column) + 1;
            return bytes;
        }

        public static long Of(TableValueDto? table)
        {
            if (table == null) return 4;
            var bytes = Header(table);
            foreach (var row in table.Rows)
                bytes += Of(row);
            return bytes;
        }

        /// <summary>An update and the comma after it.</summary>
        public static long Of(EntryUpdateDto update) =>
            EntryFixed + 1 + Of(update.ContainerId) + Of(update.EntryId) + Of(update.Type)
            + Of(update.SensorValue) + Of(update.TextValue) + Of(update.TableValue);

        /// <summary>
        /// A plugin as the catalogue lists it, and the comma after it: its own fields (name, description, settings, actions
        /// and images), without its containers.
        /// </summary>
        public static long Of(PluginMetadataDto plugin)
        {
            var bytes = MetadataFixed + 1 + Of(plugin.Id) + Of(plugin.Name) + Of(plugin.Description) + Of(plugin.ConfigFilePath);
            foreach (var action in plugin.Actions)
                bytes += ActionFixed + 1 + Of(action.MethodName) + Of(action.DisplayName);
            foreach (var setting in plugin.ConfigProperties)
            {
                bytes += SettingFixed + 1 + Of(setting.Key) + Of(setting.DisplayName) + Of(setting.Description) + Of(setting.Type)
                    + setting.Value switch
                    {
                        null => 4,
                        string text => Of(text),
                        var value => Number + Of(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
                    };
                foreach (var option in setting.Options ?? [])
                    bytes += Of(option) + 1;
            }
            foreach (var image in plugin.ImageDescriptors)
                bytes += ImageFixed + 1 + Of(image.Id) + Of(image.Name) + Of(image.MmfName);
            return bytes;
        }

        /// <summary>A catalogue entry and the comma after it.</summary>
        public static long Of(EntryDto entry) =>
            EntryFixed + 1 + Of(entry.Id) + Of(entry.Name) + Of(entry.Type)
            + Of(entry.SensorValue) + Of(entry.TextValue) + Of(entry.TableValue);
    }
}
