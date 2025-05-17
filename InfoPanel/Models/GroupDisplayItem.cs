using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Serialization;

namespace InfoPanel.Models
{
    public partial class GroupDisplayItem : DisplayItem
    {
        public ObservableCollection<DisplayItem> DisplayItems { get; } = [];

        [ObservableProperty]
        private bool _isExpanded = true;


        public override object Clone()
        {
            var clone = new GroupDisplayItem
            {
                Name = Name
            };

            foreach (var displayItem in DisplayItems)
            {
                clone.DisplayItems.Add((DisplayItem)displayItem.Clone());
            }

            return clone;
        }

        public override Rect EvaluateBounds()
        {
            throw new NotImplementedException();
        }

        public override string EvaluateColor()
        {
            throw new NotImplementedException();
        }

        public override SizeF EvaluateSize()
        {
            throw new NotImplementedException();
        }

        public override string EvaluateText()
        {
            throw new NotImplementedException();
        }

        public override (string, string) EvaluateTextAndColor()
        {
            throw new NotImplementedException();
        }
    }
}
