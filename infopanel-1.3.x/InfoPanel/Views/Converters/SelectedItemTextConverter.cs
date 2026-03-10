using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;

namespace InfoPanel
{
    public class SelectedItemTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
           if(value is DisplayItem displayItem)
            {
                return $"{displayItem.Kind} Properties";
            } else if (value is Profile profile)
            {
                return $"{profile.Name}";
            }

            return "No item selected";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
