using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Views.Components
{
    public class HWiNFOVM : ObservableObject
    {
        private string _sensorName = "No sensor selected";

        public string SensorName
        {
            get { return _sensorName; }
            set { SetProperty(ref _sensorName, value); }
        }

        private string _sensorValue = String.Empty;
        public string SensorValue
        {
            get { return _sensorValue; }
            set { SetProperty(ref _sensorValue, value); }
        }

        private UInt32 _id = 0;
        public UInt32 Id
        {
            get { return _id; }
            set { SetProperty(ref _id, value); }
        }

        private UInt32 _instance = 0;
        public UInt32 Instance
        {
            get { return _instance; }
            set { SetProperty(ref _instance, value); }
        }

        private UInt32 _entryId = 0;
        public UInt32 EntryId
        {
            get { return _entryId; }
            set { SetProperty(ref _entryId, value); }
        }
    }
}
