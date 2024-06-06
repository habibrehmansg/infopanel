using InfoPanel.Models;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for ImageProperties.xaml
    /// </summary>
    public partial class ImageProperties : UserControl
    {
        public ImageProperties()
        {
            InitializeComponent();
        }

        private void ButtonSelect_Click(object sender, RoutedEventArgs e)
        {
            if(SharedModel.Instance.SelectedItem is ImageDisplayItem imageDisplayItem)
            {
                Microsoft.Win32.OpenFileDialog openFileDialog = new()
                {
                    Multiselect = false,
                    Filter = "Image files (*.jpg, *.jpeg, *.png, *.gif)|*.jpg;*.jpeg;*.png;*.gif",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
                };
                if (openFileDialog.ShowDialog() == true)
                {
                    var profile = SharedModel.Instance.SelectedProfile;

                    if(profile != null)
                    {
                        var imageFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", profile.Guid.ToString());
                        if(!Directory.Exists(imageFolder)) {
                            Directory.CreateDirectory(imageFolder);
                        }

                        try
                        {
                            var filePath = Path.Combine(imageFolder, openFileDialog.SafeFileName);
                            File.Copy(openFileDialog.FileName, filePath, true);
                            
                            Cache.PurgeImageCache(filePath);

                            imageDisplayItem.Guid = Guid.NewGuid();
                            imageDisplayItem.RelativePath = true;
                            imageDisplayItem.Name = openFileDialog.SafeFileName;
                            imageDisplayItem.FilePath = openFileDialog.SafeFileName;
                        }
                        catch { 
                            
                        }
                    }
                }
            }
        }
    }
}
