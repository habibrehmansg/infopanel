using CommunityToolkit.Mvvm.ComponentModel;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Views.Components
{
    public class LibreSensorsVM : ObservableObject
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

        private string _sensorId = String.Empty;
        public string SensorId
        {
            get { return _sensorId; }
            set { SetProperty(ref _sensorId, value); }
        }
    }
}
