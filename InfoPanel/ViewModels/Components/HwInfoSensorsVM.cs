using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace InfoPanel.ViewModels.Components
{
    public class HwInfoSensorsVM : ObservableObject
    {
        public ObservableCollection<TreeItem> Sensors { get; set; }

        private HwInfoSensorItem? selectedItem;
        public HwInfoSensorItem? SelectedItem
        {
            get { return selectedItem; }
            set { SetProperty(ref selectedItem, value); }
        }

        public HwInfoSensorsVM()
        {
            Sensors = [];
        }

        public TreeItem? FindParentSensorItem(object id)
        {
            foreach(var sensorItem in Sensors)
            {
               if(sensorItem.Id.Equals(id))
                {
                    return sensorItem;
                }
            }

            return null;
        }
    }
}
