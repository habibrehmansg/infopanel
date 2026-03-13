using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace InfoPanel.ViewModels.Components
{
    public class HwInfoConnectionItem(int remoteIndex, string displayName)
    {
        public int RemoteIndex { get; } = remoteIndex;
        public string DisplayName { get; } = displayName;
    }

    public class HwInfoSensorsVM : ObservableObject
    {
        public ObservableCollection<TreeItem> Sensors { get; set; }
        public ObservableCollection<HwInfoConnectionItem> Connections { get; set; }

        private HwInfoConnectionItem? _selectedConnection;
        public HwInfoConnectionItem? SelectedConnection
        {
            get { return _selectedConnection; }
            set { SetProperty(ref _selectedConnection, value); }
        }

        private HwInfoSensorItem? selectedItem;
        public HwInfoSensorItem? SelectedItem
        {
            get { return selectedItem; }
            set { SetProperty(ref selectedItem, value); }
        }

        public HwInfoSensorsVM()
        {
            Sensors = [];
            Connections = [];
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
