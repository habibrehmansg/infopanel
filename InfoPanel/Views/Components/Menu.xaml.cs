using InfoPanel.Views.Components.WebServer;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for Menu.xaml
    /// </summary>
    public partial class Menu : UserControl
    {
        public Menu()
        {
            InitializeComponent();
        }

        private async void MenuItemExit_Click(object sender, RoutedEventArgs e)
        {
            await BeadaPanelTask.Instance.StopAsync();

            Environment.Exit(0);
        }

        private void MenuItemPerformanceSettings_Click(object sender, RoutedEventArgs e)
        {
            var performanceSettings = new PerformanceSettings();

            if (Application.Current is App app)
            {
                if(app.MainWindow != null)
                {
                    performanceSettings.Owner = app.MainWindow;
                }
            }

            performanceSettings.Show();
        }

        private void MenuItemDiscord_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe", "https://discord.gg/cQnjdMC7Qc");
        }

        private void MenuItemWebserverSettings_Click(object sender, RoutedEventArgs e)
        {
            var webServerSettings = new WebServerSettings();
            if (Application.Current is App app)
            {
                if (app.MainWindow != null)
                {
                    webServerSettings.Owner = app.MainWindow;
                }
            }

            webServerSettings.Show();
        }
    }

    
}
