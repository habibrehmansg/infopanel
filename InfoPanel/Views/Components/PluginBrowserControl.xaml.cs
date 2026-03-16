using InfoPanel.ApiClient;
using InfoPanel.ViewModels;
using InfoPanel.Views.Pages;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui;

namespace InfoPanel.Views.Components
{
    public partial class PluginBrowserControl : UserControl
    {
        public PluginBrowserControl()
        {
            InitializeComponent();
        }

        private void Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not PluginBrowserViewModel vm) return;
            if (sender is not ComboBox cb) return;

            vm.SelectedCategory = cb.SelectedIndex switch
            {
                1 => Category.Media,
                2 => Category.Monitoring,
                3 => Category.Utilities,
                4 => Category.Other,
                _ => null
            };
        }

        private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not PluginBrowserViewModel vm) return;
            if (sender is not ComboBox cb) return;

            vm.SelectedSort = cb.SelectedIndex switch
            {
                1 => Sort.Rating,
                2 => Sort.Name,
                3 => Sort.Newest,
                _ => Sort.Downloads
            };
        }

        private void GoToAccount_Click(object sender, RoutedEventArgs e)
        {
            var navigationService = App.GetService<INavigationService>();
            navigationService?.Navigate(typeof(AccountPage));
        }

        private void PluginCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: PluginBrowserItemViewModel item })
            {
                var page = Window.GetWindow(this)?.FindName("RootFrame") as Frame;

                // Navigate up to the PluginsPage to trigger the detail dialog
                if (this.FindParentPage() is Views.Pages.PluginsPage pluginsPage)
                {
                    pluginsPage.ShowPluginDetail(item);
                }
            }
        }
    }

    internal static class VisualTreeHelpers
    {
        public static System.Windows.Controls.Page? FindParentPage(this DependencyObject child)
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is System.Windows.Controls.Page page)
                    return page;
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return null;
        }
    }
}
