using System.Data;
using System.Diagnostics.CodeAnalysis;
using InfoPanel.Plugins.Ipc;

namespace InfoPanel.Plugins.Host
{
    internal static class TableDtoConverter
    {
        /// <summary>
        /// A plugin table as the host sends it, cut to the engine's bounds (<see cref="IpcLimits"/>: rows, columns, a
        /// cell's characters, column names) and to <paramref name="maxBytes"/> by <see cref="JsonSize"/>'s estimate, the
        /// rows past either left out; <c>Cut</c> says whether anything was. Rows past the engine's are never read.
        /// </summary>
        public static (TableValueDto Table, long Bytes, bool Cut) Convert(IPluginTable table, long maxBytes)
        {
            var format = table.DefaultFormat ?? "";
            var dto = new TableValueDto
            {
                DefaultFormat = Cut(format, IpcLimits.CellChars),
                Columns = [],
                Rows = []
            };
            var cut = format.Length > IpcLimits.CellChars;

            var dt = table.Value;
            if (dt == null) return (dto, JsonSize.Of(dto), cut);

            var columnCount = Math.Min(dt.Columns.Count, IpcLimits.TableColumns);
            cut |= dt.Columns.Count > columnCount;
            for (int i = 0; i < columnCount; i++)
            {
                var name = dt.Columns[i].ColumnName;
                cut |= name.Length > IpcLimits.NameChars;
                dto.Columns.Add(Cut(name, IpcLimits.NameChars));
            }

            var bytes = JsonSize.Header(dto);
            foreach (DataRow row in dt.Rows)
            {
                if (dto.Rows.Count == IpcLimits.TableRows)
                {
                    cut = true;
                    break;
                }

                var rowCells = new List<TableCellDto>(columnCount);
                for (int i = 0; i < columnCount; i++)
                {
                    var cell = new TableCellDto();
                    var value = row[i];
                    if (value is IPluginSensor cellSensor)
                    {
                        var name = cellSensor.Name ?? "";
                        var unit = cellSensor.Unit ?? "";
                        cut |= name.Length > IpcLimits.CellChars || unit.Length > IpcLimits.CellChars;
                        unit = Cut(unit, IpcLimits.CellChars);
                        cell.Type = "sensor";
                        cell.SensorName = Cut(name, IpcLimits.CellChars);
                        cell.SensorUnit = unit;
                        cell.SensorValue = new SensorValueDto
                        {
                            Value = cellSensor.Value,
                            ValueMin = cellSensor.ValueMin,
                            ValueMax = cellSensor.ValueMax,
                            ValueAvg = cellSensor.ValueAvg,
                            Unit = unit
                        };
                    }
                    else
                    {
                        var text = value is IPluginText cellText ? cellText.Value ?? "" : value?.ToString() ?? "";
                        cut |= text.Length > IpcLimits.CellChars;
                        cell.Type = "text";
                        cell.TextValue = Cut(text, IpcLimits.CellChars);
                    }
                    rowCells.Add(cell);
                }

                var rowBytes = JsonSize.Of(rowCells);
                if (bytes + rowBytes > maxBytes)
                {
                    cut = true;
                    break;
                }
                dto.Rows.Add(rowCells);
                bytes += rowBytes;
            }

            return (dto, bytes, cut);
        }

        /// <summary>
        /// <paramref name="value"/>'s first <paramref name="chars"/> characters, as the engine cuts what it reads (null
        /// stays null): the host makes the engine's cuts before it sends.
        /// </summary>
        [return: NotNullIfNotNull(nameof(value))]
        internal static string? Cut(string? value, int chars) => value == null || value.Length <= chars ? value : value[..chars];
    }
}
