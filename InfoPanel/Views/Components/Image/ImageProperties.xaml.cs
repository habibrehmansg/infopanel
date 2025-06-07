using InfoPanel.Models;
using System;
using System.IO;
using System.Linq;
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
            ComboBoxType.ItemsSource = Enum.GetValues(typeof(ImageDisplayItem.ImageType)).Cast<ImageDisplayItem.ImageType>();
        }

        private void ButtonSelect_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedItem is ImageDisplayItem imageDisplayItem)
            {
                Microsoft.Win32.OpenFileDialog openFileDialog = new()
                {
                    Multiselect = false,
                    Filter =
                    "All supported files|*.jpg;*.jpeg;*.png;*.svg;*.gif;*.webp;*.mp4;*.mkv;*.webm;*.avi;*.mov" +
                    "|Image files (*.jpg, *.jpeg, *.png, *.svg)|*.jpg;*.jpeg;*.png;*.svg" +
                    "|Animated files (*.gif, *.webp)|*.gif;*.webp" +
                    "|Video files (*.mp4, *.mkv, *.webm, *.avi, *.mov)|*.mp4;*.mkv;*.webm;*.avi;*.mov",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
                };
                if (openFileDialog.ShowDialog() == true)
                {
                    var profile = SharedModel.Instance.SelectedProfile;

                    if (profile != null)
                    {
                        var imageFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", profile.Guid.ToString());
                        if (!Directory.Exists(imageFolder))
                        {
                            Directory.CreateDirectory(imageFolder);
                        }

                        try
                        {
                            var fileName = openFileDialog.SafeFileName;

                            var filePath = Path.Combine(imageFolder, openFileDialog.SafeFileName);
                            File.Copy(openFileDialog.FileName, filePath, true);

                            imageDisplayItem.Guid = Guid.NewGuid();
                            imageDisplayItem.RelativePath = true;
                            imageDisplayItem.Name = openFileDialog.SafeFileName;
                            imageDisplayItem.FilePath = fileName;

                            var lockedImage = Cache.GetLocalImage(imageDisplayItem);

                            if(lockedImage != null)
                            {
                                imageDisplayItem.Width = lockedImage.Width;
                                imageDisplayItem.Height = lockedImage.Height;
                            }
                        }
                        catch
                        {

                        }
                    }
                }
            }
        }

        private void CheckBoxCache_Unchecked(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedItem is ImageDisplayItem imageDisplayItem && !imageDisplayItem.Cache
                && Cache.GetLocalImage(imageDisplayItem) is LockedImage lockedImage)
            {
                lockedImage.DisposeSKAssets();
                lockedImage.DisposeD2DAssets();
            }
        }
    }
}
