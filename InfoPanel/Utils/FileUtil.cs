using InfoPanel.Models;
using InfoPanel.Views.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace InfoPanel.Utils
{
    class FileUtil
    {
        public static string GetBundledPluginFolder()
        {
            return Path.Combine("plugins");
        }

        public static string GetExternalPluginFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InfoPanel", "plugins");
        }

        public static string GetPluginStateFile()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "plugins.bin");
        }

        public static string GetRelativeAssetPath(Profile profile, string fileName)
        {
            return GetRelativeAssetPath(profile.Guid, fileName);
        }

        public static string GetRelativeAssetPath(Guid profileGuid, string fileName)
        {
            return GetRelativeAssetPath(profileGuid.ToString(), fileName);
        }

        public static string GetRelativeAssetPath(string profileGuid, string fileName)
        {
            return Path.Combine(
                           Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "InfoPanel", "assets", profileGuid, fileName);
        }

        public static string GetAssetPath(Profile profile)
        {
            return GetAssetPath(profile.Guid);
        }

        public static string GetAssetPath(Guid profileGuid)
        {
            return GetAssetPath(profileGuid.ToString());
        }
        public static string GetAssetPath(string profileGuid)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", profileGuid);
        }
        public static string GetAssetDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets");
        }

        public static async Task<bool> SaveAsset(Profile profile, string fileName, byte[] data)
        {
            var assetPath = GetAssetPath(profile);

            if (!Directory.Exists(assetPath))
            {
                Directory.CreateDirectory(assetPath);
            }

            var filePath = Path.Combine(assetPath, fileName);

            try
            {
                await File.WriteAllBytesAsync(filePath, data);
            }
            catch
            {
                return false;
            }

            return true;
        }

        public static async Task CleanupAssets()
        {
            await Task.Run(() =>
            {
                //load from file as there may be unsaved changes
                if (ConfigModel.LoadProfilesFromFile() is List<Profile> profiles)
                {
                    try
                    {
                        var assetDirectory = GetAssetDirectory();

                        if (!Directory.Exists(assetDirectory))
                        {
                            return;
                        }

                        var assetFolders = Directory.GetDirectories(assetDirectory).ToList();

                        foreach (var profile in profiles)
                        {
                            var assetFolder = GetAssetPath(profile);
                            assetFolders.Remove(assetFolder);

                            if (Directory.Exists(assetFolder))
                            {
                                var assetFiles = Directory.GetFiles(assetFolder).ToList();

                                //load from file as there may be unsaved changes
                                if (SharedModel.LoadDisplayItemsFromFile(profile) is List<DisplayItem> displayItems)
                                {
                                    foreach (var item in displayItems)
                                    {
                                        FilterAssetFiles(item, assetFiles);
                                    }
                                }

                                //clean up removed files
                                foreach (var assetFile in assetFiles)
                                {
                                    try
                                    {
                                        File.Delete(assetFile);
                                    }
                                    catch { }
                                }
                            }
                        }

                        //clean up removed profiles
                        foreach (var assetFolder in assetFolders)
                        {
                            try
                            {
                                Directory.Delete(assetFolder, true);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            });
        }

        private static void FilterAssetFiles(DisplayItem item, List<string> assetFiles)
        {
            if (item is GroupDisplayItem groupDisplayItem)
            {
                foreach (var child in groupDisplayItem.DisplayItems)
                {
                    FilterAssetFiles(child, assetFiles);
                }
            }
            else if (item is ImageDisplayItem imageDisplayItem)
            {
                if (imageDisplayItem.CalculatedPath != null)
                {
                    assetFiles.Remove(imageDisplayItem.CalculatedPath);
                }
            }
            else if (item is GaugeDisplayItem gaugeDisplayItem)
            {
                foreach (var image in gaugeDisplayItem.Images)
                {
                    if (image.CalculatedPath != null)
                    {
                        assetFiles.Remove(image.CalculatedPath);
                    }
                }
            }
        }
    }
}
