using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static System.Environment;


namespace InfoPanel.Utils
{
    public static class PluginStateHelper
    {
        private static readonly string _infoPanelAppData = Path.Combine(GetFolderPath(SpecialFolder.LocalApplicationData), "InfoPanel");
        private static readonly string _pluginStateEncrypted = Path.Combine(_infoPanelAppData, "pluginState.bin");
        private static readonly string _infoPanelProgFiles = Path.Combine(GetFolderPath(SpecialFolder.ProgramFilesX86), "InfoPanel");
        public static readonly string PluginsFolder = Path.Combine(_infoPanelAppData, "plugins");

        /// <summary>
        /// Get a list of <see cref="PluginHash"/> from the plugins in the plugin folder
        /// </summary>
        /// <returns></returns>
        public static List<PluginHash> GetLocalPluginDllHashes()
        {
            var pluginList = new List<PluginHash>();
            var pluginsFolder = Path.Combine(PluginsFolder);
            foreach (var folder in Directory.GetDirectories(pluginsFolder))
            {
                var hashDict = new Dictionary<string, string>();
                foreach (var dll in Directory.GetFiles(folder, "*.dll"))
                {
                    var hash = BitConverter.ToString(XxHash64.Hash(File.ReadAllBytes(dll))).Replace("-", string.Empty); ;
                    hashDict.Add(Path.GetFileName(dll), hash);
                }
                var ph = new PluginHash() { PluginName = Path.GetFileName(folder), Hashes = hashDict };
                pluginList.Add(ph);
            }
            return pluginList;
        }

        /// <summary>
        /// Initial setup. Get the local plugins, and sync them into the plugin state file
        /// </summary>
        public static void InitialSetup()
        {
            var initFile = Path.Combine(_infoPanelProgFiles, "PluginInit.init");
            if(!File.Exists(initFile) && File.Exists(_pluginStateEncrypted))
            {
                throw new InvalidDataException("State file missing coressponding init file.");
            }
            else if (File.Exists(initFile) && !File.Exists(_pluginStateEncrypted))
            {
                throw new FileNotFoundException("Failed to find plugin state file");
            }
            else if (File.Exists(initFile) && File.Exists(_pluginStateEncrypted))
            {
                return;
            }
            else
            {
                var plugins = GetLocalPluginDllHashes();
                EncryptAndSaveStateList(plugins);
                File.WriteAllText(Path.Combine(_infoPanelProgFiles, "PluginInit.init"), $"STATE_FILE_CREATED:{DateTime.UtcNow}");
            }
        }

        /// <summary>
        /// Update the plugin state list, replacing with the PluginHash list
        /// </summary>
        /// <param name="plugins"></param>
        public static void UpdatePluginStateList(List<PluginHash> plugins)
        {
            EncryptAndSaveStateList(plugins);
        }

        /// <summary>
        /// Encrypt and Save the plugin state file with an updated list
        /// </summary>
        /// <param name="pluginList">List of plugins to save to state file</param>
        private static void EncryptAndSaveStateList(List<PluginHash> pluginList)
        {
            string jsonString = JsonSerializer.Serialize(pluginList);
            byte[] plainBytes = Encoding.UTF8.GetBytes(jsonString);
            byte[] encryptedBytes = EncryptData(plainBytes);
            File.WriteAllBytes(_pluginStateEncrypted, encryptedBytes);
        }

        /// <summary>
        /// Get the PluginHashes from the encrypted state file
        /// </summary>
        /// <returns></returns>
        public static List<PluginHash> DecryptAndLoadStateList()
        {
            byte[] encryptedStateBytes = File.ReadAllBytes(_pluginStateEncrypted);
            byte[] decryptedBytes = DecryptData(encryptedStateBytes);
            string decryptedJson = Encoding.UTF8.GetString(decryptedBytes);
            var pluginStateList = JsonSerializer.Deserialize<List<PluginHash>>(decryptedJson);
            if (pluginStateList == null) pluginStateList = new List<PluginHash>();
            return pluginStateList;
        }

        /// <summary>
        /// Validate the local plugins against the plugin state file.
        /// </summary>
        /// <returns>Returns a <see cref="bool"/> of if all of the plugins can be validated, and a <see cref="List{PluginHash}"/> of <see cref="PluginHash"/> for any mismatched plugins</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static Tuple<bool, List<PluginHash>> ValidateHashes()
        {
            List<PluginHash> pluginStateList = DecryptAndLoadStateList();
            List<PluginHash> localPluginList = GetLocalPluginDllHashes();

            if (pluginStateList == null || localPluginList == null)
            {
                throw new ArgumentNullException("Lists cannot be null");
            }

            List<PluginHash> mismatchedPlugins = new List<PluginHash>();
            // Compare each plugin in the local list against the state list
            foreach (var localPlugin in localPluginList)
            {
                // Find matching plugin in state list by name
                var statePlugin = pluginStateList.FirstOrDefault(p =>
                    p.PluginName == localPlugin.PluginName);

                
                // Compare hashes
                var mismatchedHashes = new Dictionary<string, string>();
                foreach (var localHash in localPlugin.Hashes)
                {
                    if (!statePlugin.Hashes.TryGetValue(localHash.Key, out string stateHash) ||
                        stateHash != localHash.Value)
                    {
                        mismatchedHashes[localHash.Key] = localHash.Value;
                    }
                }

                // If there are any mismatches, add to result
                if (mismatchedHashes.Count > 0)
                {
                    mismatchedPlugins.Add(new PluginHash
                    {
                        PluginName = localPlugin.PluginName,
                        Activated = localPlugin.Activated,
                        Hashes = mismatchedHashes
                    });
                }
            }
            return new Tuple<bool, List<PluginHash>>(mismatchedPlugins.Count == 0,mismatchedPlugins);
        }        

        static byte[] EncryptData(byte[] data)
        {
            // Optional entropy adds additional randomness
            byte[] entropy = Encoding.UTF8.GetBytes("MySecretEntropy");

            // Encrypt using CurrentUser scope
            return ProtectedData.Protect(
                data,                    // Data to encrypt
                entropy,                 // Optional entropy
                DataProtectionScope.CurrentUser // Scope of protection
            );
        }
        static byte[] DecryptData(byte[] encryptedData)
        {
            byte[] entropy = Encoding.UTF8.GetBytes("MySecretEntropy");

            // Decrypt using CurrentUser scope
            return ProtectedData.Unprotect(
                encryptedData,
                entropy,
                DataProtectionScope.CurrentUser
            );
        }
    }
}
