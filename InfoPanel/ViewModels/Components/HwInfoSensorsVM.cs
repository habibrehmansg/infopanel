using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.ViewModels.Components
{
    public class HwInfoSensorsVM : ObservableObject
    {
        private string _sensorName = "No sensor selected";

        public string SensorName
        {
            get { return _sensorName; }
            set { SetProperty(ref _sensorName, value); }
        }

        private string _sensorValue = string.Empty;
        public string SensorValue
        {
            get { return _sensorValue; }
            set { SetProperty(ref _sensorValue, value); }
        }

        private uint _id = 0;
        public uint Id
        {
            get { return _id; }
            set { SetProperty(ref _id, value); }
        }

        private uint _instance = 0;
        public uint Instance
        {
            get { return _instance; }
            set { SetProperty(ref _instance, value); }
        }

        private uint _entryId = 0;
        public uint EntryId
        {
            get { return _entryId; }
            set { SetProperty(ref _entryId, value); }
        }
    }
}
