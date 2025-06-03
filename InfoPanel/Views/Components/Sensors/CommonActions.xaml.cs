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
            var item = new TextDisplayItem("Custom Text")
            {
                Font = SharedModel.Instance.SelectedProfile!.Font,
                FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                Color = SharedModel.Instance.SelectedProfile!.Color
            };
            SharedModel.Instance.AddDisplayItem(item);
        }

        private void ButtonNewImage_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile != null)
            {
                var item = new ImageDisplayItem("Image", SharedModel.Instance.SelectedProfile.Guid)
                {
                    Width = 100,
                    Height = 100
                };
                SharedModel.Instance.AddDisplayItem(item);
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
        }

        private void ButtonNewShape_Click(object sender, RoutedEventArgs e)
        {
            var item = new ShapeDisplayItem("Shape");
            SharedModel.Instance.AddDisplayItem(item);
        }
    }
}
