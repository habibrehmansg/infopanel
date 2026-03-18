using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using System.Windows;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for DesignPage.xaml
    /// </summary>
    public partial class DesignPage
    {
        public DesignViewModel ViewModel
        {
            get;
        }

        public DesignPage(DesignViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;
            InitializeComponent();
            Unloaded += DesignPage_Unloaded;
        }

        private void DesignPage_Unloaded(object sender, RoutedEventArgs e)
        {
            SharedModel.Instance.SelectedItem = null;
        }

        [RelayCommand]
        private static void Unselect()
        {
            if (SharedModel.Instance.SelectedItem is DisplayItem selectedItem)
            {
                selectedItem.Selected = false;
            }
        }

        private void ShowItemsPanel_Click(object sender, RoutedEventArgs e)
        {
            ItemsPanelOverlay.Visibility = Visibility.Visible;
            ShowItemsPanelButton.Visibility = Visibility.Collapsed;
            SensorAreaBorder.Margin = new Thickness(0, 60, 580, 0);
        }

        private void HideItemsPanel_Click(object sender, RoutedEventArgs e)
        {
            ItemsPanelOverlay.Visibility = Visibility.Collapsed;
            ShowItemsPanelButton.Visibility = Visibility.Visible;
            SensorAreaBorder.Margin = new Thickness(0, 60, 0, 0);
        }
    }
}
