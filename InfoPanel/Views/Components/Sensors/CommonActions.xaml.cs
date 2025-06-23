using InfoPanel.Models;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for CommonActions.xaml
    /// </summary>
    public partial class CommonActions : UserControl
    {
        public CommonActions()
        {
            InitializeComponent();
        }

        private void ButtonNewText_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new TextDisplayItem("Custom Text", selectedProfile)
                {
                    Font = selectedProfile.Font,
                    FontSize = selectedProfile.FontSize,
                    Color = selectedProfile.Color
                };
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonNewImage_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new ImageDisplayItem("Image", selectedProfile);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonNewClock_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new ClockDisplayItem("Clock", selectedProfile)
                {
                    Font = SharedModel.Instance.SelectedProfile!.Font,
                    FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                    Color = SharedModel.Instance.SelectedProfile!.Color

                };
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonNewCalendar_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new CalendarDisplayItem("Calendar", selectedProfile)
                {
                    Font = SharedModel.Instance.SelectedProfile!.Font,
                    FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                    Color = SharedModel.Instance.SelectedProfile!.Color
                };
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonNewShape_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new ShapeDisplayItem("Shape", selectedProfile);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonGroup_Click(object sender, RoutedEventArgs e)
        {
            var groupDisplayItem = new GroupDisplayItem
            {
                Name = "New Group",
            };

            var selectedItem = SharedModel.Instance.SelectedItem;

            SharedModel.Instance.AddDisplayItem(groupDisplayItem);

            if (selectedItem is not null)
            {
                SharedModel.Instance.PushDisplayItemTo(groupDisplayItem, selectedItem);
            }
        }
    }
}
