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
        /// Update the plugin state file from the <see cref="PluginHash"/> parameter
        /// </summary>
        /// <param name="pluginHash"></param>
        public static void SetPluginState(PluginHash pluginHash)
        {
            var pluginStateList = DecryptAndLoadStateList();
            var idx = pluginStateList.FindIndex(x => x.PluginName == pluginHash.PluginName);
            if (idx != -1)
            {
                pluginStateList[idx] = pluginHash;
            }
            EncryptAndSaveStateList(pluginStateList);
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

            // Create a dictionary from the newList for faster lookup
            var localPluginDict = localPluginList.ToDictionary(x => x.PluginName, x => x.Hashes);

            List<PluginHash> mismatchedPlugins = new List<PluginHash>();
            foreach (var masterItem in pluginStateList)
            {
                if (!localPluginDict.TryGetValue(masterItem.PluginName, out var newHashes))
                {
                    continue; // Skip if no matching plugin name found
                }

                if (!AreDictionariesEqual(masterItem.Hashes, newHashes))
                {
                    mismatchedPlugins.Add(masterItem);
                }
            }
            var result = new Tuple<bool, List<PluginHash>>(mismatchedPlugins.Count == 0, mismatchedPlugins);
            return result;
        }

        /// <summary>
        /// Validate a <see cref="PluginHash"/> against the plugin state file.
        /// </summary>
        /// <param name="pluginHash">PluginHash you want to validate against</param>
        /// <returns>Returns a <see cref="bool"/> of if all of the plugins can be validated, and a <see cref="PluginHash"/> if the plugin is mismatched</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static Tuple<bool, PluginHash> ValidateHashes(PluginHash pluginHash)
        {
            List<PluginHash> pluginStateList = DecryptAndLoadStateList();

            if (pluginStateList == null || pluginHash == null)
            {
                throw new ArgumentNullException("Lists cannot be null");
            }

            bool isValid = false;
            PluginHash statePluginHash = new PluginHash();
            foreach (var masterItem in pluginStateList)
            {
                if (masterItem.PluginName == pluginHash.PluginName)
                {
                    isValid = AreDictionariesEqual(masterItem.Hashes, pluginHash.Hashes); 
                    if(!isValid) statePluginHash = masterItem;
                }
            }
            var result = new Tuple<bool, PluginHash>(isValid, statePluginHash);
            return result;
        }

        private static bool AreDictionariesEqual(Dictionary<string, string> sourceDict, Dictionary<string, string> testingDict)
        {
            // Handle null cases
            if (sourceDict == null && testingDict == null)
            {
                return true;
            }
            if (sourceDict == null || testingDict == null)
            {
                return false;
            }

            // Check count
            if (sourceDict.Count != testingDict.Count)
            {
                return false;
            }

            // Check key-value pairs
            return sourceDict.All(kvp =>
                testingDict.TryGetValue(kvp.Key, out var value) &&
                value == kvp.Value);
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
