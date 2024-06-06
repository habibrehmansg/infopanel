using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace InfoPanel
{
    /// <summary>
    /// Interaction logic for ProfileWindow.xaml
    /// </summary>
    public partial class ProfileWindow : Window
    {
        public Profile Profile { get; set; }

        public bool DeletionAllowed
        {
            get
            {
                return ConfigModel.Instance.Profiles.Count > 1;
            }
        }
        public ProfileWindow(Profile profile)
        {
            Profile = profile;
            InitializeComponent();
        }

        private void ButtonSave_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ConfigModel.Instance.SaveProfiles();
            Close();
        }

        private void ButtonExportProfile_Click(object sender, RoutedEventArgs e)
        {
            FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
            if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string selectedFolderPath = folderBrowserDialog.SelectedPath;
                string? result = SharedModel.Instance.ExportProfile(Profile, selectedFolderPath);
                if(result != null)
                {
                }
            }
        }

        private void ButtonDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ConfigModel.Instance.RemoveProfile(Profile))
            {
                var newSelectedProfile = ConfigModel.Instance.Profiles.FirstOrDefault(profile => { return profile.Active; }, ConfigModel.Instance.Profiles[0]);
                SharedModel.Instance.SelectedProfile = newSelectedProfile;
                ConfigModel.Instance.SaveProfiles();
                Close();
            }
        }

        private void ButtonResetPosition_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var screen = Screen.PrimaryScreen;
            Profile.TargetWindow = new TargetWindow(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            Profile.WindowX = 0;
            Profile.WindowY = 0;
        }

        private void ButtonMaximise_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is App app)
            {
                app.MaximiseDisplayWindow(Profile);
            }
        }

        private void ButtonReload_Click(object sender, RoutedEventArgs e)
        {
            ConfigModel.Instance.ReloadProfile(Profile);
        }
    }
}
