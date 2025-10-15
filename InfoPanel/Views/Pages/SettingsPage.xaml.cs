using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Services;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using System;
using Serilog;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for SettingsPage.xaml
    /// </summary>
    public partial class SettingsPage : Page
    {
        private static readonly ILogger Logger = Log.ForContext<SettingsPage>();
        public SettingsViewModel ViewModel { get; }


        public SettingsPage(SettingsViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;
            InitializeComponent();
            ComboBoxListenIp.Items.Add("127.0.0.1");
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface ni in interfaces)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    IPInterfaceProperties ipProps = ni.GetIPProperties();
                    foreach (IPAddressInformation addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !addr.Address.ToString().StartsWith("169.254."))
                        {
                            ComboBoxListenIp.Items.Add(addr.Address.ToString());
                        }
                    }
                }
            }

            ComboBoxListenPort.Items.Add("80");
            ComboBoxListenPort.Items.Add("81");
            ComboBoxListenPort.Items.Add("2020");
            ComboBoxListenPort.Items.Add("8000");
            ComboBoxListenPort.Items.Add("8008");
            ComboBoxListenPort.Items.Add("8080");
            ComboBoxListenPort.Items.Add("8081");
            ComboBoxListenPort.Items.Add("8088");
            ComboBoxListenPort.Items.Add("10000");
            ComboBoxListenPort.Items.Add("10001");

            ComboBoxRefreshRate.Items.Add(16);
            ComboBoxRefreshRate.Items.Add(33);
            ComboBoxRefreshRate.Items.Add(50);
            ComboBoxRefreshRate.Items.Add(66);
            ComboBoxRefreshRate.Items.Add(100);
            ComboBoxRefreshRate.Items.Add(200);
            ComboBoxRefreshRate.Items.Add(300);
            ComboBoxRefreshRate.Items.Add(500);
            ComboBoxRefreshRate.Items.Add(1000);
        }

        private void ButtonOpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel");
            Process.Start(new ProcessStartInfo("explorer.exe", path));
        }

        private async void ButtonCheckPawnIO_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Information("Checking PawniO status from Settings page");

                // Refresh status
                ViewModel.RefreshPawnIOStatus();

                // If not installed or requires update, offer to install/update
                if (!PawnIoHelper.IsInstalled || PawnIoHelper.RequiresUpdate)
                {
                    string message;
                    if (PawnIoHelper.RequiresUpdate)
                    {
                        message = $"PawniO is outdated (v{PawnIoHelper.Version}).\n\n" +
                                 $"LibreHardwareMonitor requires PawniO v2.0.0.0 or higher.\n\n" +
                                 "Would you like to update it now?";
                    }
                    else
                    {
                        message = "PawniO is not installed.\n\n" +
                                 "LibreHardwareMonitor requires PawniO for low-level hardware access.\n\n" +
                                 "Would you like to install it now?";
                    }

                    var result = MessageBox.Show(
                        message,
                        "PawniO Driver",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.OK)
                    {
                        bool success = PawnIoHelper.InstallOrUpdate();

                        if (success && PawnIoHelper.IsInstalled)
                        {
                            ViewModel.RefreshPawnIOStatus();

                            // Restart LibreMonitor if it's running to load the new driver
                            if (ConfigModel.Instance.Settings.LibreHardwareMonitor)
                            {
                                Logger.Information("Restarting LibreMonitor to load PawniO driver");
                                await LibreMonitor.Instance.StopAsync();
                                await LibreMonitor.Instance.StartAsync();
                                Logger.Information("LibreMonitor restarted successfully");
                            }

                            MessageBox.Show(
                                $"PawniO v{PawnIoHelper.Version} has been installed successfully.\n\n" +
                                "LibreHardwareMonitor has been restarted to load the driver.",
                                "Installation Complete",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    }
                }
                else
                {
                    MessageBox.Show(
                        $"PawniO v{PawnIoHelper.Version} is installed and up to date.",
                        "PawniO Status",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error checking PawniO status");
                MessageBox.Show(
                    "An error occurred while checking PawniO status. See logs for details.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
