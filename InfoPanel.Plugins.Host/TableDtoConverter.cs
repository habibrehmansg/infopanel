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
        public static (TableValueDto Table, long Bytes, bool Cut) Convert(IPluginTable table, long maxBytes) =>
            Convert(table.DefaultFormat, table.Value, maxBytes);

        /// <summary>
        /// <see cref="Convert(IPluginTable, long)"/> of a table's <paramref name="defaultFormat"/> and
        /// <paramref name="dt"/>, read once by the caller.
        /// </summary>
        public static (TableValueDto Table, long Bytes, bool Cut) Convert(string? defaultFormat, DataTable? dt, long maxBytes)
        {
            var format = defaultFormat ?? "";
            var dto = new TableValueDto
            {
                DefaultFormat = Cut(format, IpcLimits.CellChars),
                Columns = [],
                Rows = []
            };
            var cut = format.Length > IpcLimits.CellChars;

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

                var rowCells = Row(row, columnCount, ref cut);
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

        // A row's first columnCount cells as Convert sends them, cut to the engine's bounds; cut is set when one was.
        private static List<TableCellDto> Row(DataRow row, int columnCount, ref bool cut)
        {
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
                    var read = PluginSensorReading.Of(cellSensor);
                    cell.SensorValue = new SensorValueDto
                    {
                        Value = read.Value,
                        ValueMin = read.ValueMin,
                        ValueMax = read.ValueMax,
                        ValueAvg = read.ValueAvg,
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
            return rowCells;
        }

        /// <summary>
        /// Whether <see cref="Convert(string, DataTable, long)"/> with <paramref name="maxBytes"/> would make
        /// <paramref name="delivered"/> of this table, found by reading it as Convert does, allocating nothing (but a non-text
        /// cell's text). Equal cells come to equal bytes, so the delivered rows' bytes are what Convert counts. When
        /// <paramref name="delivered"/> stops short of the table's rows and of <see cref="IpcLimits.TableRows"/>, the first
        /// row left out is built, alone, to tell whether it still doesn't fit.
        /// </summary>
        public static bool Matches(string? defaultFormat, DataTable? dt, TableValueDto delivered, long maxBytes)
        {
            if (!SameCut(defaultFormat ?? "", IpcLimits.CellChars, delivered.DefaultFormat)) return false;
            if (dt == null) return delivered.Columns.Count == 0 && delivered.Rows.Count == 0;

            var columnCount = Math.Min(dt.Columns.Count, IpcLimits.TableColumns);
            if (delivered.Columns.Count != columnCount) return false;
            for (int i = 0; i < columnCount; i++)
            {
                if (!SameCut(dt.Columns[i].ColumnName, IpcLimits.NameChars, delivered.Columns[i])) return false;
            }

            var bytes = JsonSize.Header(delivered);
            var r = 0;
            foreach (DataRow row in dt.Rows)
            {
                if (r == delivered.Rows.Count)
                {
                    if (r == IpcLimits.TableRows) return true;
                    var cut = false;
                    return bytes + JsonSize.Of(Row(row, columnCount, ref cut)) > maxBytes;
                }
                var cells = delivered.Rows[r++];
                if (cells.Count != columnCount) return false;
                for (int i = 0; i < columnCount; i++)
                {
                    if (!SameCell(row[i], cells[i])) return false;
                }
                bytes += JsonSize.Of(cells);
                if (bytes > maxBytes) return false;
            }
            return r == delivered.Rows.Count;
        }

        // A cell as Convert makes it, compared with the delivered one.
        private static bool SameCell(object? value, TableCellDto cell)
        {
            if (value is IPluginSensor sensor)
            {
                var sent = cell.SensorValue;
                if (cell.Type != "sensor" || cell.TextValue != null || sent == null) return false;
                var read = PluginSensorReading.Of(sensor);
                return SameCut(sensor.Name ?? "", IpcLimits.CellChars, cell.SensorName)
                    && SameCut(sensor.Unit ?? "", IpcLimits.CellChars, cell.SensorUnit)
                    && sent.Unit == cell.SensorUnit
                    && sent.Value.Equals(read.Value)
                    && sent.ValueMin.Equals(read.ValueMin)
                    && sent.ValueMax.Equals(read.ValueMax)
                    && sent.ValueAvg.Equals(read.ValueAvg);
            }

            var text = value is IPluginText cellText ? cellText.Value ?? "" : value?.ToString() ?? "";
            return cell.Type == "text" && cell.SensorValue == null && cell.SensorName == null && cell.SensorUnit == null
                && SameCut(text, IpcLimits.CellChars, cell.TextValue);
        }

        // Whether Cut(value, chars) is cut.
        private static bool SameCut(string value, int chars, string? cut) =>
            cut != null && value.AsSpan(0, Math.Min(value.Length, chars)).SequenceEqual(cut);

        /// <summary>
        /// <paramref name="value"/>'s first <paramref name="chars"/> characters, as the engine cuts what it reads (null
        /// stays null): the host makes the engine's cuts before it sends.
        /// </summary>
        [return: NotNullIfNotNull(nameof(value))]
        internal static string? Cut(string? value, int chars) => value == null || value.Length <= chars ? value : value[..chars];
    }
}
