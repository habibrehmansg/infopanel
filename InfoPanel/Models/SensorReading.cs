using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Models
{
    public struct SensorReading
    {
        public SensorReading(double valueMin, double valueMax, double valueAvg, double valueNow, string unit)
        {
            ValueMin = valueMin;
            ValueMax = valueMax;
            ValueAvg = valueAvg;
            ValueNow = valueNow;
            Unit = unit;
        }

        public SensorReading(float valueMin, float valueMax, float valueAvg, float valueNow, string unit)
        {
            ValueMin = valueMin;
            ValueMax = valueMax;
            ValueAvg = valueAvg;
            ValueNow = valueNow;
            Unit = unit;
        }

        public SensorReading(string value)
        {
            ValueText = value;
            Unit = string.Empty;
        }
        public SensorReading(DataTable valueTable, string valueTableFormat, string valueText)
        {
            ValueTable = valueTable;
            ValueTableFormat = valueTableFormat;
            ValueText = valueText;
            Unit = string.Empty;
        }

        public double ValueMin;
        public double ValueMax;
        public double ValueAvg;
        public double ValueNow;
        public string? ValueText;
        public DataTable? ValueTable;
        public string? ValueTableFormat;
        public string Unit;
    }
}
