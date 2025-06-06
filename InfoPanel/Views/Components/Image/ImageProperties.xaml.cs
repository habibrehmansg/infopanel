using InfoPanel.Models;
using InfoPanel.Views.Windows;
using System;
using System.Collections.Generic;
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
         }

        private async void ButtonSelect_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedItem is ImageDisplayItem imageDisplayItem)
            {
                Microsoft.Win32.OpenFileDialog openFileDialog = new()
                {
                    Multiselect = false,
                    Filter =
                    "All supported files|*.jpg;*.jpeg;*.png;*.svg;*.gif;*.webp;*.mp4" +
                    "|Image files (*.jpg, *.jpeg, *.png, *.svg)|*.jpg;*.jpeg;*.png;*.svg" +
                    "|Animated files (*.gif, *.webp)|*.gif;*.webp" +
                    "|Video files (*.mp4)|*.mp4;",
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

                            if (openFileDialog.SafeFileName.EndsWith(".mp4"))
                            {
                                var loadingWindow = new LoadingWindow
                                {
                                    Owner = App.Current.MainWindow
                                };
                                loadingWindow.SetText("Processing video..");
                                loadingWindow.Show();

                                fileName = $"{fileName}.webp";
                                var webPFilePath = Path.Combine(imageFolder, fileName);
                                await VideoBackgroundHelper.GenerateWebP(openFileDialog.FileName, webPFilePath);
                                loadingWindow.Close();

                                imageDisplayItem.Cache = false;
                            }
                            else
                            {
                                var filePath = Path.Combine(imageFolder, openFileDialog.SafeFileName);
                                File.Copy(openFileDialog.FileName, filePath, true);
                            }

                            //OptimizeGif(filePath);

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

        //public static void OptimizeGif(string filePath, int optimalFrameCount = 60)
        //{
        //    // Load the GIF as a collection
        //    var collection = new MagickImageCollection(filePath);

        //    if (collection.Count > 1)
        //    {
        //        // Optimize the GIF by coalescing
        //        collection.Coalesce();

        //        // Calculate the original total duration of the GIF
        //        int originalTotalDuration = 0;
        //        foreach (var frame in collection)
        //        {
        //            originalTotalDuration += (int)frame.AnimationDelay;
        //        }

        //        if (collection.Count > optimalFrameCount)
        //        {
        //            // Calculate frames to keep
        //            List<int> framesToKeep = new List<int>();
        //            int frameCount = collection.Count;
        //            int step = frameCount / optimalFrameCount;

        //            // Add the indices of the frames to keep
        //            for (int i = 0; i < frameCount; i += step)
        //            {
        //                framesToKeep.Add(i);
        //            }

        //            // Ensure exactly 30 frames are kept
        //            while (framesToKeep.Count > optimalFrameCount)
        //            {
        //                framesToKeep.RemoveAt(framesToKeep.Count - 1);
        //            }

        //            // Remove frames not in the framesToKeep list
        //            for (int i = collection.Count - 1; i >= 0; i--)
        //            {
        //                if (!framesToKeep.Contains(i))
        //                {
        //                    collection.RemoveAt(i);
        //                }
        //            }

        //            // Calculate new delay to keep the same total animation duration
        //            int newTotalDuration = originalTotalDuration;
        //            int newDelay = newTotalDuration / collection.Count;

        //            // Adjust the delay of each remaining frame
        //            foreach (var frame in collection)
        //            {
        //                frame.AnimationDelay = (uint)newDelay;
        //            }
        //        }

        //        // Write the optimized GIF back to the file
        //        collection.Write(filePath);
        //    }
        //}
    }
}
