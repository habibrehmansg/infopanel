using InfoPanel.Models;
using InfoPanel.ViewModels.Components;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for HWiNFOSensors.xaml
    /// </summary>
    public partial class HwInfoSensors : System.Windows.Controls.UserControl
    {
        private HwInfoSensorsVM ViewModel { get; set; }

        private readonly DispatcherTimer UpdateTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        public HwInfoSensors()
        {
            ViewModel = new HwInfoSensorsVM();
            DataContext = ViewModel;

            InitializeComponent();

            Loaded += HwInfoSensors_Loaded;
            Unloaded += HwInfoSensors_Unloaded;
        }

        private void HwInfoSensors_Loaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Tick += Timer_Tick;

                Timer_Tick(this, new EventArgs());
                UpdateTimer.Start();
            }
        }

        private void HwInfoSensors_Unloaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Stop();
                UpdateTimer.Tick -= Timer_Tick;
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdateConnectionList();
            LoadSensorTree();
            UpdateSensorDetails();
        }

        private void UpdateConnectionList()
        {
            var available = HWHash.GetAvailableConnections();

            // Check if the list changed
            bool changed = available.Count != ViewModel.Connections.Count;
            if (!changed)
            {
                for (int i = 0; i < available.Count; i++)
                {
                    if (ViewModel.Connections[i].RemoteIndex != available[i].remoteIndex)
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                var previousSelection = ViewModel.SelectedConnection?.RemoteIndex;
                ViewModel.Connections.Clear();
                foreach (var (remoteIndex, name) in available)
                {
                    ViewModel.Connections.Add(new HwInfoConnectionItem(remoteIndex, name));
                }

                // Restore previous selection or default to local
                if (previousSelection.HasValue)
                {
                    ViewModel.SelectedConnection = ViewModel.Connections.FirstOrDefault(c => c.RemoteIndex == previousSelection.Value);
                }
                ViewModel.SelectedConnection ??= ViewModel.Connections.FirstOrDefault(c => c.RemoteIndex == -1)
                    ?? ViewModel.Connections.FirstOrDefault();
            }

            // Auto-select if nothing selected
            if (ViewModel.SelectedConnection == null && ViewModel.Connections.Count > 0)
            {
                ViewModel.SelectedConnection = ViewModel.Connections.FirstOrDefault(c => c.RemoteIndex == -1)
                    ?? ViewModel.Connections.FirstOrDefault();
            }
        }

        private void LoadSensorTree()
        {
            if (ViewModel.SelectedConnection == null)
                return;

            int remoteIndex = ViewModel.SelectedConnection.RemoteIndex;

            foreach (HWHash.HWINFO_HASH hash in HWHash.GetOrderedList(remoteIndex))
            {
                //construct parent
                var parent = ViewModel.FindParentSensorItem(hash.ParentUniqueID);
                if(parent == null)
                {
                    var parentName = hash.ParentNameDefault;

                    // Strip "[Desktop-xxx] " prefix for display — already shown in the connection combobox
                    if (remoteIndex >= 0 && parentName.StartsWith('['))
                    {
                        var endBracket = parentName.IndexOf("] ");
                        if (endBracket > 0)
                        {
                            parentName = parentName[(endBracket + 2)..];
                        }
                    }

                    parent = new TreeItem(hash.ParentUniqueID, parentName);
                    ViewModel.Sensors.Add(parent);
                }

                TreeItem? group;
                if(hash.ReadingType != "Other" && hash.ReadingType != "None")
                {
                    //construct type grouping
                    group = parent.FindChild(hash.ReadingType);

                    if (group == null)
                    {
                        group = new TreeItem(hash.ReadingType, hash.ReadingType);
                        parent.Children.Add(group);
                    }
                } else
                {
                    group = parent;
                }

                //construct actual sensor
                var child = group.FindChild(hash.UniqueID);
                if (child == null)
                {
                    child = new HwInfoSensorItem(hash.UniqueID, hash.NameDefault, remoteIndex, hash.ParentID, hash.ParentInstance, hash.SensorID);
                    group.Children.Add(child);
                }
            }
        }

        private void ConnectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Clear and rebuild tree when connection changes
            ViewModel.Sensors.Clear();
            ViewModel.SelectedItem = null;
            LoadSensorTree();
        }

        private void TreeViewInfo_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is HwInfoSensorItem sensorItem)
            {
                ViewModel.SelectedItem = sensorItem;
                sensorItem.Update();
            }
            else
            {
                ViewModel.SelectedItem = null;
            }
        }

        private void UpdateSensorDetails()
        {
            ViewModel.SelectedItem?.Update();
        }


        private void ImageLogo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Process.Start("explorer.exe", "https://www.hwinfo.com/");
        }
    }
}
