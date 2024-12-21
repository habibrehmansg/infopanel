using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Extensions
{
    public static class DoubleExtensions
    {
        public static string ToFormattedString(this double value, string format = "F2")
        {
            // Check if the value is a whole number
            if (value == Math.Truncate(value))
            {
                // If it's a whole number, format without decimals
                return value.ToString("F0");
            }
            else
            {
                // Otherwise, format with up to two decimal places
                return value.ToString(format).TrimEnd('0').TrimEnd('.');
            }
        }
    }
}
