using InfoPanel.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InfoPanel.Views.Components
{
    public partial class PluginDetailControl : UserControl
    {
        public PluginDetailControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is PluginDetailViewModel vm)
            {
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(PluginDetailViewModel.UserRating))
                    {
                        UpdateStars(vm.UserRating);
                    }
                };
                UpdateStars(vm.UserRating);
            }
        }

        private void UpdateStars(double? rating)
        {
            var stars = new[] { Star1, Star2, Star3, Star4, Star5 };
            var filledBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)); // Gold
            var emptyBrush = (Brush)FindResource("TextFillColorTertiaryBrush");

            for (int i = 0; i < stars.Length; i++)
            {
                var isFilled = rating.HasValue && i < (int)rating.Value;
                stars[i].Foreground = isFilled ? filledBrush : emptyBrush;
                stars[i].Filled = isFilled;
            }
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is PluginDetailViewModel vm)
            {
                await vm.DownloadAsync();
            }
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is PluginDetailViewModel vm)
            {
                await vm.UninstallAsync();
            }
        }

        private async void Rate1_Click(object sender, RoutedEventArgs e) => await RateAsync(1);
        private async void Rate2_Click(object sender, RoutedEventArgs e) => await RateAsync(2);
        private async void Rate3_Click(object sender, RoutedEventArgs e) => await RateAsync(3);
        private async void Rate4_Click(object sender, RoutedEventArgs e) => await RateAsync(4);
        private async void Rate5_Click(object sender, RoutedEventArgs e) => await RateAsync(5);

        private async System.Threading.Tasks.Task RateAsync(int rating)
        {
            if (DataContext is PluginDetailViewModel vm)
            {
                await vm.RateAsync(rating);
            }
        }
    }
}
