using InfoPanel.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for DisplayItems.xaml
    /// </summary>
    public partial class DisplayItems : System.Windows.Controls.UserControl
    {
        private DisplayItem? SelectedItem { get { return SharedModel.Instance.SelectedItem; } }
        public DisplayItems()
        {
            InitializeComponent();
            Unloaded += DisplayItems_Unloaded;
            SharedModel.Instance.PropertyChanged += Instance_PropertyChanged;
        }

        private void DisplayItems_Unloaded(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.PropertyChanged -= Instance_PropertyChanged;
        }

        private void Instance_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SharedModel.Instance.SelectedItem))
            {
                if (SelectedItem != null)
                {
                    ListViewItems.ScrollIntoView(SelectedItem);
                }
            }
        }

        private void ButtonPushUp_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem != null)
            {
                SharedModel.Instance.PushDisplayItemBy(SelectedItem, -1);
                ListViewItems.ScrollIntoView(SelectedItem);
            }
        }

        private void ButtonPushDown_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem != null)
            {
                SharedModel.Instance.PushDisplayItemBy(SelectedItem, 1);
                ListViewItems.ScrollIntoView(SelectedItem);
            }
        }

        private void ButtonPushBack_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem != null)
            {
                SharedModel.Instance.PushDisplayItemTo(SelectedItem, 0);
                ListViewItems.ScrollIntoView(SelectedItem);
            }
        }

        private void ButtonPushFront_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem != null)
            {
                SharedModel.Instance.PushDisplayItemTo(SelectedItem, SharedModel.Instance.GetProfileDisplayItemsCopy().Count - 1);
                ListViewItems.ScrollIntoView(SelectedItem);
            }
        }

        private void ButtonDelete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem != null)
            {
                SharedModel.Instance.RemoveDisplayItem(SelectedItem);
            }
        }

        private void ButtonReload_Click(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.LoadDisplayItems();
        }

        private void ButtonSave_Click(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.SaveDisplayItems();
        }

        private void ButtonNewText_Click(object sender, RoutedEventArgs e)
        {
            var item = new TextDisplayItem("Custom Text")
            {
                Font = SharedModel.Instance.SelectedProfile!.Font,
                FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                Color = SharedModel.Instance.SelectedProfile!.Color
            };
            SharedModel.Instance.AddDisplayItem(item);
            SharedModel.Instance.SelectedItem = item;

        }

        private void ButtonNewImage_Click(object sender, RoutedEventArgs e)
        {
            if(SharedModel.Instance.SelectedProfile != null)
            {
                var item = new ImageDisplayItem("Image", SharedModel.Instance.SelectedProfile.Guid);
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonNewClock_Click(object sender, RoutedEventArgs e)
        {
            var item = new ClockDisplayItem("Clock")
            {
                Font = SharedModel.Instance.SelectedProfile!.Font,
                FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                Color = SharedModel.Instance.SelectedProfile!.Color
                
            };
            SharedModel.Instance.AddDisplayItem(item);
            SharedModel.Instance.SelectedItem = item;
        }

        private void ButtonNewCalendar_Click(object sender, RoutedEventArgs e)
        {
            var item = new CalendarDisplayItem("Calendar")
            {
                Font = SharedModel.Instance.SelectedProfile!.Font,
                FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                Color = SharedModel.Instance.SelectedProfile!.Color
            };
            SharedModel.Instance.AddDisplayItem(item);
            SharedModel.Instance.SelectedItem = item;

        }

        private void ButtonDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedItem != null)
            {
                var item = (DisplayItem)SharedModel.Instance.SelectedItem.Clone();
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.PushDisplayItemTo(item, SharedModel.Instance.SelectedItem);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ListViewItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SharedModel.Instance.SelectedItems?.ForEach(item =>
            {
                if(!ListViewItems.SelectedItems.Contains(item))
                {
                    item.Selected = false;
                }
            });

            ListViewItems.ScrollIntoView(SharedModel.Instance.SelectedItem);
        }
    }
}
