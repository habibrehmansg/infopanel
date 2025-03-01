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
        private static readonly string _pluginStateEncrypted = Path.Combine("PluginState.dat");

        /// <summary>
        /// Get a list of <see cref="PluginHash"/> from the plugins in the plugin folder
        /// </summary>
        /// <returns></returns>
        public static List<PluginHash> GetLocalPluginDllHashes()
        {
            var pluginList = new List<PluginHash>();

            foreach (var folder in Directory.GetDirectories(FileUtil.GetBundledPluginFolder()))
            {
                //hash not required for bundled
                var ph = new PluginHash() { PluginFolder = Path.GetFileName(folder), Bundled = true, };
                pluginList.Add(ph);
            }


            foreach (var folder in Directory.GetDirectories(FileUtil.GetExternalPluginFolder()))
            {
                var hash = HashPlugin(Path.GetFileName(folder));
                var ph = new PluginHash() { PluginFolder = Path.GetFileName(folder), Hash = hash };
                pluginList.Add(ph);
            }
            return pluginList;
        }

        public static string? HashPlugin(string pluginName)
        {
            var folder = Path.Combine(FileUtil.GetExternalPluginFolder(), pluginName);

            if (Directory.Exists(folder))
            {
                using var hashAlgorithm = SHA256.Create();
                using var memoryStream = new MemoryStream();

                foreach (var dll in Directory.GetFiles(folder, "*.dll"))
                {
                    using var stream = File.OpenRead(dll);
                    var fileHash = hashAlgorithm.ComputeHash(stream);
                    memoryStream.Write(fileHash, 0, fileHash.Length);
                }

                memoryStream.Position = 0;
                var finalHash = hashAlgorithm.ComputeHash(memoryStream);

                return BitConverter.ToString(finalHash).Replace("-", "").ToLowerInvariant();
            }

            return null;
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
            try
            {
                byte[] encryptedStateBytes = File.ReadAllBytes(_pluginStateEncrypted);
                byte[] decryptedBytes = DecryptData(encryptedStateBytes);
                string decryptedJson = Encoding.UTF8.GetString(decryptedBytes);
                var pluginStateList = JsonSerializer.Deserialize<List<PluginHash>>(decryptedJson);
                if (pluginStateList != null)
                {
                    return pluginStateList;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }


            return [];
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

            List<PluginHash> mismatchedPlugins = [];
            // Compare each plugin in the local list against the state list
            foreach (var localPlugin in localPluginList)
            {
                // Find matching plugin in state list by name
                var statePlugin = pluginStateList.FirstOrDefault(p =>
                p.Bundled == localPlugin.Bundled && p.PluginFolder == localPlugin.PluginFolder);
                if (statePlugin == null) continue;

                // Compare hashes
                var mismatchedHashes = new Dictionary<string, string>();

                if (statePlugin.Hash != localPlugin.Hash)
                {
                    mismatchedPlugins.Add(new PluginHash
                    {
                        PluginFolder = localPlugin.PluginFolder,
                        Activated = localPlugin.Activated
                    });
                }

            }
            return new Tuple<bool, List<PluginHash>>(mismatchedPlugins.Count == 0, mismatchedPlugins);
        }

        public static void UpdateValidation()
        {
            var validation = ValidateHashes();
            if (validation.Item1 == true || validation.Item2.Count == 0) return;
            var pluginState = DecryptAndLoadStateList();
            foreach (var mismatchHash in validation.Item2)
            {
                if (mismatchHash == null) continue;
                var idx = pluginState.FindIndex(x => x.PluginFolder == mismatchHash.PluginFolder);
                if (idx != -1)
                {
                    pluginState[idx].Activated = false;
                }
            }
            EncryptAndSaveStateList(pluginState);
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
