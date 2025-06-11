using InfoPanel.Models;
using InfoPanel.ViewModels.Components;
using System;
using System.Diagnostics;
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
            LoadSensorTree();
            UpdateSensorDetails();
        }

        private void LoadSensorTree()
        {
            foreach (HWHash.HWINFO_HASH hash in HWHash.GetOrderedList())
            {
                //construct parent
                var parent = ViewModel.FindParentSensorItem(hash.ParentUniqueID);
                if(parent == null)
                {
                    parent = new TreeItem(hash.ParentUniqueID, hash.ParentNameDefault);
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
                    child = new HwInfoSensorItem(hash.UniqueID, hash.NameDefault, hash.ParentID, hash.ParentInstance, hash.SensorID);
                    group.Children.Add(child);
                }
            }
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

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = (ScrollViewer)sender;
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
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
