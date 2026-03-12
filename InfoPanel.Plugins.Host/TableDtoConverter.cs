using System.Data;
using InfoPanel.Plugins.Ipc;

namespace InfoPanel.Plugins.Host
{
    internal static class TableDtoConverter
    {
        public static TableValueDto ConvertTableToDto(IPluginTable table)
        {
            var dto = new TableValueDto
            {
                DefaultFormat = table.DefaultFormat,
                Columns = [],
                Rows = []
            };

            var dt = table.Value;
            foreach (DataColumn col in dt.Columns)
            {
                dto.Columns.Add(col.ColumnName);
            }

            foreach (DataRow row in dt.Rows)
            {
                var rowCells = new List<TableCellDto>();
                for (int i = 0; i < dt.Columns.Count; i++)
                {
                    var cell = new TableCellDto();
                    var value = row[i];
                    if (value is IPluginSensor cellSensor)
                    {
                        cell.Type = "sensor";
                        cell.SensorName = cellSensor.Name;
                        cell.SensorUnit = cellSensor.Unit;
                        cell.SensorValue = new SensorValueDto
                        {
                            Value = cellSensor.Value,
                            ValueMin = cellSensor.ValueMin,
                            ValueMax = cellSensor.ValueMax,
                            ValueAvg = cellSensor.ValueAvg,
                            Unit = cellSensor.Unit
                        };
                    }
                    else if (value is IPluginText cellText)
                    {
                        cell.Type = "text";
                        cell.TextValue = cellText.Value;
                    }
                    else
                    {
                        cell.Type = "text";
                        cell.TextValue = value?.ToString() ?? "";
                    }
                    rowCells.Add(cell);
                }
                dto.Rows.Add(rowCells);
            }

            return dto;
        }
    }
}
