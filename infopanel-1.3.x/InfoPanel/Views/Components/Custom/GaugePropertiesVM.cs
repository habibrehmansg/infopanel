using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Views.Components.Custom
{
    public class GaugePropertiesVM: ObservableObject
    {
        private ImageDisplayItem? _selectedItem;

        public ImageDisplayItem? SelectedItem
        {
            get { return _selectedItem; }
            set 
            { 
                SetProperty(ref _selectedItem, value);
                OnPropertyChanged(nameof(ItemSelected));    
            }
        }

        public bool ItemSelected { get { return _selectedItem != null; } }
    }
}
