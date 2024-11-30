using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.ViewModels.Components;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for HWiNFOSensors.xaml
    /// </summary>
    public partial class LibreSensors : System.Windows.Controls.UserControl
    {
        private LibreSensorsVM ViewModel { get; set; }

        private Timer? UpdateTimer;

        public LibreSensors()
        {
            ViewModel = new LibreSensorsVM();
            DataContext = ViewModel;

            InitializeComponent();

            Loaded += LibreSensors_Loaded;
            Unloaded += LibreSensors_Unloaded;

            UpdateTimer = new Timer
            {
                Interval = 1000
            };
            UpdateTimer.Tick += Timer_Tick;

            //tick once
            Timer_Tick(this, null);
            UpdateTimer.Start();
        }

        private void LibreSensors_Loaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Tick += Timer_Tick;
                UpdateTimer.Start();
            }
        }

        private void LibreSensors_Unloaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Stop();
                UpdateTimer.Tick -= Timer_Tick;
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (TreeViewInfo.Items.Count > 0)
            {
                UpdateSensorDetails();
            }
            else
            {
                LoadLibreSensors();
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = (ScrollViewer)sender;
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        public void LoadLibreSensors()
        {
            TreeViewInfo.Items.Clear();

            var parentDict = new Dictionary<Identifier, TreeViewItem>();

            foreach (ISensor hash in LibreMonitor.GetOrderedList())
            {
                TreeViewItem item;

                var identifier = hash.Hardware.Parent?.Identifier ?? hash.Hardware.Identifier;

                if (parentDict.ContainsKey(identifier))
                {
                    item = parentDict[identifier];
                }
                else
                {
                    item = new TreeViewItem();
                    item.SetResourceReference(TreeViewItem.ForegroundProperty, "TextFillColorSecondaryBrush");
                    item.Focusable = false;
                    item.Header = hash.Hardware.Parent?.Name ?? hash.Hardware.Name;
                    //item.Tag = hash.Index;
                    item.Selected += delegate (object sender, RoutedEventArgs e)
                    {
                        TreeViewItem selectedItem = (TreeViewItem)TreeViewInfo.SelectedItem;
                        Trace.WriteLine("Selected " + selectedItem.Tag);

                        if (selectedItem.Parent is TreeViewItem)
                        {
                            GridActions.IsEnabled = true;
                        }
                        else
                        {
                            GridActions.IsEnabled = false;
                        }
                    };

                    parentDict.Add(identifier, item);
                    TreeViewInfo.Items.Add(item);
                }

                TreeViewItem subItem = new();
                subItem.SetResourceReference(TreeViewItem.ForegroundProperty, "TextFillColorTertiaryBrush");
                subItem.Focusable = false;
                subItem.PreviewMouseDown += SubItem_PreviewMouseDown;
                subItem.Header = hash.Name;
                subItem.Tag = hash.Identifier;

                bool added = false;
                foreach(TreeViewItem group in item.Items)
                {
                    if(group.Name == hash.SensorType.ToString())
                    {
                        group.Items.Add(subItem);
                        added = true;
                        break;
                    }
                }

                if(!added)
                {
                    TreeViewItem group = new();
                    group.SetResourceReference(TreeViewItem.ForegroundProperty, "TextFillColorTertiaryBrush");
                    group.Focusable = false;
                    group.Name = hash.SensorType.ToString();
                    group.Header = hash.SensorType.ToString();
                    group.Items.Add(subItem);
                    item.Items.Add(group);
                }


            }
        }

        private void SubItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {   
            ((TreeViewItem) sender).IsSelected = true;
        }

        private void UpdateSensorDetails()
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;

            if (selectedTreeViewItem?.Tag is Identifier identifier)
            {
                if(LibreMonitor.SENSORHASH.TryGetValue(identifier.ToString(), out ISensor? sensor))
                {
                    var item = new SensorDisplayItem(sensor.Name, sensor.Identifier.ToString());

                    ViewModel.SensorName = item.Name;
                    ViewModel.SensorId = item.LibreSensorId;
                    ViewModel.SensorValue = item.EvaluateText();
                }
            }
            else
            {
                ViewModel.SensorName = "No sensor selected";
                ViewModel.SensorId = String.Empty;
                ViewModel.SensorValue = String.Empty;
            }
        }

        private void TreeViewInfo_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            UpdateSensorDetails();
        }

        private void ButtonSelect_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Tag is Identifier identifier)
            {
                if (LibreMonitor.SENSORHASH.TryGetValue(identifier.ToString(), out ISensor? sensor))
                {
                    var item = new SensorDisplayItem(sensor.Name, sensor.Identifier.ToString())
                    {
                        SensorName = sensor.Name,
                        Font = SharedModel.Instance.SelectedProfile!.Font,
                        FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                        Color = SharedModel.Instance.SelectedProfile!.Color,
                        Unit = sensor.GetUnit(),
                    };
                    
                    SharedModel.Instance.AddDisplayItem(item);
                    SharedModel.Instance.SelectedItem = item;
                }
            }
        }

        private void ButtonReplace_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Tag is Identifier identifier)
            {
                if (LibreMonitor.SENSORHASH.TryGetValue(identifier.ToString(), out ISensor? sensor))
                {
                    if (SharedModel.Instance.SelectedItem is SensorDisplayItem sensorDisplayItem)
                    {
                        sensorDisplayItem.Name = (string)selectedTreeViewItem.Header;
                        sensorDisplayItem.SensorName = (string)selectedTreeViewItem.Header;
                        sensorDisplayItem.SensorType = Models.SensorType.Libre;
                        sensorDisplayItem.LibreSensorId = sensor.Identifier.ToString();
                        sensorDisplayItem.Unit = sensor.GetUnit();
                    }
                    else if (SharedModel.Instance.SelectedItem is ChartDisplayItem chartDisplayItem)
                    {
                        chartDisplayItem.Name = (string)selectedTreeViewItem.Header;
                        chartDisplayItem.SensorName = (string)selectedTreeViewItem.Header;
                        chartDisplayItem.SensorType = Models.SensorType.Libre;
                        chartDisplayItem.LibreSensorId = sensor.Identifier.ToString();
                    }
                    else if (SharedModel.Instance.SelectedItem is GaugeDisplayItem gaugeDisplayItem)
                    {
                        gaugeDisplayItem.Name = (string)selectedTreeViewItem.Header;
                        gaugeDisplayItem.SensorName = (string)selectedTreeViewItem.Header;
                        gaugeDisplayItem.SensorType = Models.SensorType.Libre;
                        gaugeDisplayItem.LibreSensorId = sensor.Identifier.ToString();
                    }
                }
            }
        }

        private void ButtonAddGraph_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Tag is Identifier identifier)
            {
                var item = new GraphDisplayItem((string)selectedTreeViewItem.Header, GraphDisplayItem.GraphType.LINE, identifier.ToString());
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonAddBar_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Tag is Identifier identifier)
            {
                var item = new BarDisplayItem((string)selectedTreeViewItem.Header, identifier.ToString());
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonAddDonut_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Tag is Identifier identifier)
            {
                var item = new DonutDisplayItem((string)selectedTreeViewItem.Header, identifier.ToString());
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonAddCustom_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Tag is Identifier identifier)
            {
                var item = new GaugeDisplayItem((string)selectedTreeViewItem.Header, identifier.ToString());
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }
    }
}
