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
    public class CustomStepIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            ImageDisplayItem? imageDisplayItem = value as ImageDisplayItem;

            if (imageDisplayItem != null)
            {
                if (SharedModel.Instance.SelectedItem is GaugeDisplayItem customDisplayItem)
                {
                    var step = 100.0 / (customDisplayItem.Images.Count - 1);
                    var index = customDisplayItem.Images.IndexOf(imageDisplayItem);

                    int startValue = (int)Math.Ceiling(index * step);
                    int endValue = startValue + (int)(step);

                    if (index == customDisplayItem.Images.Count - 1)
                    {
                        return $"≤100%";
                    }
                    else
                    {
                        return $"≤{endValue - 1}%";
                    }
                }
            }

            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
