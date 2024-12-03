using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace InfoPanel.ViewModels.Components
{
    public partial class LibreSensorsVM : ObservableObject
    {
        public ObservableCollection<TreeItem> Sensors { get; set; }

        private LibreSensorItem? selectedItem;
        public LibreSensorItem? SelectedItem
        {
            get { return selectedItem; }
            set { SetProperty(ref selectedItem, value); }
        }

        public LibreSensorsVM()
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
