using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace InfoPanel.ViewModels.Components
{
    public partial class PluginSensorsVM : ObservableObject
    {
        public ObservableCollection<TreeItem> Sensors { get; set; }

        private PluginSensorItem? selectedItem;
        public PluginSensorItem? SelectedItem
        {
            get { return selectedItem; }
            set { SetProperty(ref selectedItem, value); }
        }

        public PluginSensorsVM()
        {
            Sensors = [];
        }

        public TreeItem? FindParentSensorItem(object id)
        {
            foreach (var sensorItem in Sensors)
            {
                if (sensorItem.Id.Equals(id))
                {
                    return sensorItem;
                }
            }

            return null;
        }
    }
}
