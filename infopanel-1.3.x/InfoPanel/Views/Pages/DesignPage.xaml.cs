
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
    }
}
