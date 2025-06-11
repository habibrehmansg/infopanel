using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.ViewModels.Components;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for SensorActions.xaml
    /// </summary>
    public partial class SensorActions : UserControl
    {
        public static readonly DependencyProperty SelectedSensorItemProperty =
            DependencyProperty.Register(nameof(SelectedSensorItem), typeof(SensorTreeItem), typeof(SensorActions), new PropertyMetadata(null));

        public static readonly DependencyProperty SensorTypeProperty =
            DependencyProperty.Register(nameof(SensorType), typeof(SensorType), typeof(SensorActions), new PropertyMetadata(SensorType.HwInfo));

        public SensorTreeItem SelectedSensorItem
        {
            get { return (SensorTreeItem)GetValue(SelectedSensorItemProperty); }
            set { SetValue(SelectedSensorItemProperty, value); }
        }

        public SensorType SensorType
        {
            get { return (SensorType)GetValue(SensorTypeProperty); }
            set { SetValue(SensorTypeProperty, value); }
        }

        public SensorActions()
        {
            InitializeComponent();
        }

        private void ButtonSelect_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSensorItem == null || SharedModel.Instance.SelectedProfile is not Profile selectedProfile)
                return;

            SensorDisplayItem item;

            switch (SensorType)
            {
                case SensorType.HwInfo:
                    if (SelectedSensorItem is HwInfoSensorItem hwInfoItem)
                    {
                        item = new SensorDisplayItem(hwInfoItem.Name, selectedProfile, hwInfoItem.ParentId, hwInfoItem.ParentInstance, hwInfoItem.SensorId)
                        {
                            Font = selectedProfile.Font,
                            FontSize = selectedProfile.FontSize,
                            Color = selectedProfile.Color,
                            Unit = hwInfoItem.Unit,
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Libre:
                    if (SelectedSensorItem is LibreSensorItem libreItem)
                    {
                        item = new SensorDisplayItem(libreItem.Name, selectedProfile, libreItem.SensorId)
                        {
                            Font = selectedProfile.Font,
                            FontSize = selectedProfile.FontSize,
                            Color = selectedProfile.Color,
                            Unit = libreItem.Unit,
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Plugin:
                    if (SelectedSensorItem is PluginSensorItem pluginItem)
                    {
                        item = new SensorDisplayItem(pluginItem.Name, selectedProfile)
                        {
                            SensorType = SensorType.Plugin,
                            PluginSensorId = pluginItem.SensorId,
                            Font = selectedProfile.Font,
                            FontSize = selectedProfile.FontSize,
                            Color = selectedProfile.Color,
                            Unit = pluginItem.Unit,
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;
            }
        }

        private void ButtonReplace_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSensorItem == null)
                return;

            var selectedDisplayItem = SharedModel.Instance.SelectedItem;
            if (selectedDisplayItem == null)
                return;

            switch (SensorType)
            {
                case SensorType.HwInfo:
                    if (SelectedSensorItem is HwInfoSensorItem hwInfoItem)
                    {
                        ReplaceHwInfoSensor(hwInfoItem, selectedDisplayItem);
                    }
                    break;

                case SensorType.Libre:
                    if (SelectedSensorItem is LibreSensorItem libreItem)
                    {
                        ReplaceLibreSensor(libreItem, selectedDisplayItem);
                    }
                    break;

                case SensorType.Plugin:
                    if (SelectedSensorItem is PluginSensorItem pluginItem)
                    {
                        ReplacePluginSensor(pluginItem, selectedDisplayItem);
                    }
                    break;
            }
        }

        private void ReplaceHwInfoSensor(HwInfoSensorItem sensorItem, DisplayItem displayItem)
        {
            if (displayItem is SensorDisplayItem sensorDisplayItem)
            {
                sensorDisplayItem.Name = sensorItem.Name;
                sensorDisplayItem.SensorName = sensorItem.Name;
                sensorDisplayItem.SensorType = SensorType.HwInfo;
                sensorDisplayItem.Id = sensorItem.ParentId;
                sensorDisplayItem.Instance = sensorItem.ParentInstance;
                sensorDisplayItem.EntryId = sensorItem.SensorId;
                sensorDisplayItem.Unit = sensorItem.Unit;
            }
            else if (displayItem is ChartDisplayItem chartDisplayItem)
            {
                chartDisplayItem.Name = sensorItem.Name;
                chartDisplayItem.SensorName = sensorItem.Name;
                chartDisplayItem.SensorType = SensorType.HwInfo;
                chartDisplayItem.Id = sensorItem.ParentId;
                chartDisplayItem.Instance = sensorItem.ParentInstance;
                chartDisplayItem.EntryId = sensorItem.SensorId;
            }
            else if (displayItem is GaugeDisplayItem gaugeDisplayItem)
            {
                gaugeDisplayItem.Name = sensorItem.Name;
                gaugeDisplayItem.SensorName = sensorItem.Name;
                gaugeDisplayItem.SensorType = SensorType.HwInfo;
                gaugeDisplayItem.Id = sensorItem.ParentId;
                gaugeDisplayItem.Instance = sensorItem.ParentInstance;
                gaugeDisplayItem.EntryId = sensorItem.SensorId;
            }
            else if (displayItem is SensorImageDisplayItem sensorImageDisplayItem)
            {
                sensorImageDisplayItem.Name = sensorItem.Name;
                sensorImageDisplayItem.SensorName = sensorItem.Name;
                sensorImageDisplayItem.SensorType = SensorType.HwInfo;
                sensorImageDisplayItem.Id = sensorItem.ParentId;
                sensorImageDisplayItem.Instance = sensorItem.ParentInstance;
                sensorImageDisplayItem.EntryId = sensorItem.SensorId;
            }
        }

        private void ReplaceLibreSensor(LibreSensorItem sensorItem, DisplayItem displayItem)
        {
            if (displayItem is SensorDisplayItem sensorDisplayItem)
            {
                sensorDisplayItem.Name = sensorItem.Name;
                sensorDisplayItem.SensorName = sensorItem.Name;
                sensorDisplayItem.SensorType = SensorType.Libre;
                sensorDisplayItem.LibreSensorId = sensorItem.SensorId;
                sensorDisplayItem.Unit = sensorItem.Unit;
            }
            else if (displayItem is ChartDisplayItem chartDisplayItem)
            {
                chartDisplayItem.Name = sensorItem.Name;
                chartDisplayItem.SensorName = sensorItem.Name;
                chartDisplayItem.SensorType = SensorType.Libre;
                chartDisplayItem.LibreSensorId = sensorItem.SensorId;
            }
            else if (displayItem is GaugeDisplayItem gaugeDisplayItem)
            {
                gaugeDisplayItem.Name = sensorItem.Name;
                gaugeDisplayItem.SensorName = sensorItem.Name;
                gaugeDisplayItem.SensorType = SensorType.Libre;
                gaugeDisplayItem.LibreSensorId = sensorItem.SensorId;
            }
            else if (displayItem is SensorImageDisplayItem sensorImageDisplayItem)
            {
                sensorImageDisplayItem.Name = sensorItem.Name;
                sensorImageDisplayItem.SensorName = sensorItem.Name;
                sensorImageDisplayItem.SensorType = SensorType.Libre;
                sensorImageDisplayItem.LibreSensorId = sensorItem.SensorId;
            }
        }

        private void ReplacePluginSensor(PluginSensorItem sensorItem, DisplayItem displayItem)
        {
            if (displayItem is SensorDisplayItem sensorDisplayItem)
            {
                sensorDisplayItem.Name = sensorItem.Name;
                sensorDisplayItem.SensorName = sensorItem.Name;
                sensorDisplayItem.SensorType = SensorType.Plugin;
                sensorDisplayItem.PluginSensorId = sensorItem.SensorId;
                sensorDisplayItem.Unit = sensorItem.Unit;
            }
            else if (displayItem is ChartDisplayItem chartDisplayItem)
            {
                chartDisplayItem.Name = sensorItem.Name;
                chartDisplayItem.SensorName = sensorItem.Name;
                chartDisplayItem.SensorType = SensorType.Plugin;
                chartDisplayItem.PluginSensorId = sensorItem.SensorId;
            }
            else if (displayItem is GaugeDisplayItem gaugeDisplayItem)
            {
                gaugeDisplayItem.Name = sensorItem.Name;
                gaugeDisplayItem.SensorName = sensorItem.Name;
                gaugeDisplayItem.SensorType = SensorType.Plugin;
                gaugeDisplayItem.PluginSensorId = sensorItem.SensorId;
            }
            else if (displayItem is SensorImageDisplayItem sensorImageDisplayItem)
            {
                sensorImageDisplayItem.Name = sensorItem.Name;
                sensorImageDisplayItem.SensorName = sensorItem.Name;
                sensorImageDisplayItem.SensorType = SensorType.Plugin;
                sensorImageDisplayItem.PluginSensorId = sensorItem.SensorId;
            }
            else if (displayItem is HttpImageDisplayItem httpImageDisplayItem)
            {
                httpImageDisplayItem.Name = sensorItem.Name;
                httpImageDisplayItem.SensorName = sensorItem.Name;
                httpImageDisplayItem.SensorType = SensorType.Plugin;
                httpImageDisplayItem.PluginSensorId = sensorItem.SensorId;
            }
            else if (displayItem is TableSensorDisplayItem tableSensorDisplayItem)
            {
                tableSensorDisplayItem.Name = sensorItem.Name;
                tableSensorDisplayItem.SensorName = sensorItem.Name;
                tableSensorDisplayItem.SensorType = SensorType.Plugin;
                tableSensorDisplayItem.PluginSensorId = sensorItem.SensorId;
                if (SensorReader.ReadPluginSensor(sensorItem.SensorId) is SensorReading sensorReading && sensorReading.ValueTableFormat is string format)
                {
                    tableSensorDisplayItem.TableFormat = format;
                }
            }
        }

        private void ButtonAddGraph_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSensorItem == null || SharedModel.Instance.SelectedProfile is not Profile selectedProfile)
                return;

            GraphDisplayItem item;

            switch (SensorType)
            {
                case SensorType.HwInfo:
                    if (SelectedSensorItem is HwInfoSensorItem hwInfoItem)
                    {
                        item = new GraphDisplayItem(hwInfoItem.Name, selectedProfile, GraphDisplayItem.GraphType.LINE, 
                            hwInfoItem.ParentId, hwInfoItem.ParentInstance, hwInfoItem.SensorId);
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Libre:
                    if (SelectedSensorItem is LibreSensorItem libreItem)
                    {
                        item = new GraphDisplayItem(libreItem.Name, selectedProfile, GraphDisplayItem.GraphType.LINE, libreItem.SensorId);
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Plugin:
                    if (SelectedSensorItem is PluginSensorItem pluginItem)
                    {
                        item = new GraphDisplayItem(pluginItem.Name, selectedProfile, GraphDisplayItem.GraphType.LINE)
                        {
                            PluginSensorId = pluginItem.SensorId,
                            SensorType = SensorType.Plugin
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;
            }
        }

        private void ButtonAddBar_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSensorItem == null || SharedModel.Instance.SelectedProfile is not Profile selectedProfile)
                return;

            BarDisplayItem item;

            switch (SensorType)
            {
                case SensorType.HwInfo:
                    if (SelectedSensorItem is HwInfoSensorItem hwInfoItem)
                    {
                        item = new BarDisplayItem(hwInfoItem.Name, selectedProfile, hwInfoItem.ParentId, hwInfoItem.ParentInstance, hwInfoItem.SensorId);
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Libre:
                    if (SelectedSensorItem is LibreSensorItem libreItem)
                    {
                        item = new BarDisplayItem(libreItem.Name, selectedProfile, libreItem.SensorId);
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Plugin:
                    if (SelectedSensorItem is PluginSensorItem pluginItem)
                    {
                        item = new BarDisplayItem(pluginItem.Name, selectedProfile)
                        {
                            PluginSensorId = pluginItem.SensorId,
                            SensorType = SensorType.Plugin
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;
            }
        }

        private void ButtonAddDonut_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSensorItem == null || SharedModel.Instance.SelectedProfile is not Profile selectedProfile)
                return;

            DonutDisplayItem item;

            switch (SensorType)
            {
                case SensorType.HwInfo:
                    if (SelectedSensorItem is HwInfoSensorItem hwInfoItem)
                    {
                        item = new DonutDisplayItem(hwInfoItem.Name, selectedProfile, hwInfoItem.ParentId, hwInfoItem.ParentInstance, hwInfoItem.SensorId);
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Libre:
                    if (SelectedSensorItem is LibreSensorItem libreItem)
                    {
                        item = new DonutDisplayItem(libreItem.Name, selectedProfile, libreItem.SensorId);
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Plugin:
                    if (SelectedSensorItem is PluginSensorItem pluginItem)
                    {
                        item = new DonutDisplayItem(pluginItem.Name, selectedProfile)
                        {
                            PluginSensorId = pluginItem.SensorId,
                            SensorType = SensorType.Plugin
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;
            }
        }

        private void ButtonAddCustom_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSensorItem == null || SharedModel.Instance.SelectedProfile is not Profile selectedProfile)
                return;

            GaugeDisplayItem item;

            switch (SensorType)
            {
                case SensorType.HwInfo:
                    if (SelectedSensorItem is HwInfoSensorItem hwInfoItem)
                    {
                        item = new GaugeDisplayItem(hwInfoItem.Name, selectedProfile, hwInfoItem.ParentId, hwInfoItem.ParentInstance, hwInfoItem.SensorId);
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Libre:
                    if (SelectedSensorItem is LibreSensorItem libreItem)
                    {
                        item = new GaugeDisplayItem(libreItem.Name, selectedProfile, libreItem.SensorId);
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Plugin:
                    if (SelectedSensorItem is PluginSensorItem pluginItem)
                    {
                        item = new GaugeDisplayItem(pluginItem.Name, selectedProfile)
                        {
                            PluginSensorId = pluginItem.SensorId,
                            SensorType = SensorType.Plugin
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;
            }
        }

        private void ButtonAddSensorImage_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSensorItem == null || SharedModel.Instance.SelectedProfile is not Profile selectedProfile)
                return;

            SensorImageDisplayItem item;

            switch (SensorType)
            {
                case SensorType.HwInfo:
                    if (SelectedSensorItem is HwInfoSensorItem hwInfoItem)
                    {
                        item = new SensorImageDisplayItem(hwInfoItem.Name, selectedProfile, hwInfoItem.ParentId, hwInfoItem.ParentInstance, hwInfoItem.SensorId)
                        {
                            Width = 100,
                            Height = 100,
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Libre:
                    if (SelectedSensorItem is LibreSensorItem libreItem)
                    {
                        item = new SensorImageDisplayItem(libreItem.Name, selectedProfile, libreItem.SensorId)
                        {
                            Width = 100,
                            Height = 100,
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;

                case SensorType.Plugin:
                    if (SelectedSensorItem is PluginSensorItem pluginItem)
                    {
                        item = new SensorImageDisplayItem(pluginItem.Name, selectedProfile)
                        {
                            Width = 100,
                            Height = 100,
                            PluginSensorId = pluginItem.SensorId,
                            SensorType = SensorType.Plugin
                        };
                        SharedModel.Instance.AddDisplayItem(item);
                    }
                    break;
            }
        }
    }
}