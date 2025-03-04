//using InfoPanel.Models;
//using NeoSmart.SecureStore;
//using Newtonsoft.Json;
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Net;
//using System.Security.AccessControl;
//using System.Security.Cryptography;
//using System.Security.Principal;


//namespace InfoPanel.Utils
//{
//    public static class PluginStateHelper
//    {
//        public static readonly string _pluginStateEncrypted = Path.Combine(FileUtil.GetExternalPluginFolder(),"PluginState.dat");
//        private const string PLUGIN_KEY = "PluginKey.bin";
//        private const string PLUGIN_LIST = "PLUGIN_LIST";

//        /// <summary>
//        /// Get a list of <see cref="PluginHash"/> from the plugins in the plugin folder
//        /// </summary>
//        /// <returns></returns>
//        public static List<PluginHash> GetLocalPluginDllHashes()
//        {
//            var pluginList = new List<PluginHash>();

//            foreach (var folder in Directory.GetDirectories(FileUtil.GetBundledPluginFolder()))
//            {
//                //hash not required for bundled
//                var ph = new PluginHash() { PluginFolder = Path.GetFileName(folder), Bundled = true, };
//                pluginList.Add(ph);
//            }


//            foreach (var folder in Directory.GetDirectories(FileUtil.GetExternalPluginFolder()))
//            {
//                var hash = HashPlugin(Path.GetFileName(folder));
//                var ph = new PluginHash() { PluginFolder = Path.GetFileName(folder), Hash = hash };
//                pluginList.Add(ph);
//            }
//            return pluginList;
//        }

//        public static string? HashPlugin(string pluginName)
//        {
//            var folder = Path.Combine(FileUtil.GetExternalPluginFolder(), pluginName);

//            if (Directory.Exists(folder))
//            {
//                using var hashAlgorithm = SHA256.Create();
//                using var memoryStream = new MemoryStream();

//                foreach (var dll in Directory.GetFiles(folder, "*.dll"))
//                {
//                    using var stream = File.OpenRead(dll);
//                    var fileHash = hashAlgorithm.ComputeHash(stream);
//                    memoryStream.Write(fileHash, 0, fileHash.Length);
//                }

//                memoryStream.Position = 0;
//                var finalHash = hashAlgorithm.ComputeHash(memoryStream);

//                return BitConverter.ToString(finalHash).Replace("-", "").ToLowerInvariant();
//            }

//            return null;
//        }

//        public static void GeneratePluginListInitial()
//        {
//            var pluginHashes = GetLocalPluginDllHashes();
//            using (var sman = SecretsManager.CreateStore())
//            {
//                // Create a new key securely with a CSPRNG:
//                sman.GenerateKey();

//                // Optionally export the keyfile (even if you created the store with a password)
//                sman.ExportKey(PLUGIN_KEY);

//                // Then save the store if you've made any changes to it
//                sman.SaveStore(_pluginStateEncrypted);
//            }           
//            // Step 2: Create a new FileSecurity object for setting permissions
//            FileSecurity fileSecurity = new FileSecurity();

//            // Step 3: Disable inheritance and remove inherited rules
//            fileSecurity.SetAccessRuleProtection(true, false); // true = disable inheritance, false = don't copy inherited rules

//            // Step 4: Create a rule to allow Administrators full control
//            FileSystemAccessRule adminRule = new FileSystemAccessRule(
//                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
//                FileSystemRights.FullControl, // Full control for Administrators
//                AccessControlType.Allow);

//            // Step 5: Add the rule for Administrators
//            fileSecurity.AddAccessRule(adminRule);

//            // Step 8: Apply the new security settings to the file using FileInfo
//            FileInfo fileInfo = new FileInfo(PLUGIN_KEY);
//            fileInfo.SetAccessControl(fileSecurity);
//        }

//        public static void EncryptAndSaveStateList(List<PluginHash> pluginList)
//        {
//            if (!File.Exists(PLUGIN_KEY)) throw new FileNotFoundException();
//            using (var sman = SecretsManager.CreateStore())
//            {
//                sman.LoadKeyFromFile(PLUGIN_KEY);
//                var json = JsonConvert.SerializeObject(pluginList);
//                sman.Set(PLUGIN_LIST, json);
//                sman.SaveStore(_pluginStateEncrypted);
//            }
//        }

//        public static List<PluginHash> DecryptAndLoadStateList()
//        {
//            if (!File.Exists(PLUGIN_KEY)) throw new FileNotFoundException();
//            string json = string.Empty;
//            bool ok = false;
//            using (var sman = SecretsManager.LoadStore(_pluginStateEncrypted))
//            {
//                sman.LoadKeyFromFile(PLUGIN_KEY);
//                ok = sman.TryGetValue(PLUGIN_LIST, out json);
//            }
//            if (ok && json != null)
//            {
//                var pluginList = JsonConvert.DeserializeObject<List<PluginHash>>(json);
//                if(pluginList != null)
//                {
//                    return pluginList;
//                }
//                return [];
//            }
//            else
//            {
//                return [];
//            }
//        }        

//        /// <summary>
//        /// Validate the local plugins against the plugin state file.
//        /// </summary>
//        /// <returns>Returns a <see cref="bool"/> of if all of the plugins can be validated, and a <see cref="List{PluginHash}"/> of <see cref="PluginHash"/> for any mismatched plugins</returns>
//        /// <exception cref="ArgumentNullException"></exception>
//        public static Tuple<bool, List<PluginHash>> ValidateHashes()
//        {
//            List<PluginHash> pluginStateList = DecryptAndLoadStateList();
//            List<PluginHash> localPluginList = GetLocalPluginDllHashes();

//            if (pluginStateList == null || localPluginList == null)
//            {
//                throw new ArgumentNullException("Lists cannot be null");
//            }

//            List<PluginHash> mismatchedPlugins = [];
//            // Compare each plugin in the local list against the state list
//            foreach (var localPlugin in localPluginList)
//            {
//                // Find matching plugin in state list by name
//                var statePlugin = pluginStateList.FirstOrDefault(p =>
//                p.Bundled == localPlugin.Bundled && p.PluginFolder == localPlugin.PluginFolder);
//                if (statePlugin == null) continue;

//                // Compare hashes
//                var mismatchedHashes = new Dictionary<string, string>();

//                if (statePlugin.Hash != localPlugin.Hash)
//                {
//                    mismatchedPlugins.Add(new PluginHash
//                    {
//                        PluginFolder = localPlugin.PluginFolder,
//                        Activated = localPlugin.Activated
//                    });
//                }

//            }
//            return new Tuple<bool, List<PluginHash>>(mismatchedPlugins.Count == 0, mismatchedPlugins);
//        }

//        public static void UpdateValidation()
//        {
//            var validation = ValidateHashes();
//            if (validation.Item1 == true || validation.Item2.Count == 0) return;
//            var pluginState = DecryptAndLoadStateList();
//            foreach (var mismatchHash in validation.Item2)
//            {
//                if (mismatchHash == null) continue;
//                var idx = pluginState.FindIndex(x => x.PluginFolder == mismatchHash.PluginFolder);
//                if (idx != -1)
//                {
//                    pluginState[idx].Activated = false;
//                }
//            }
//            EncryptAndSaveStateList(pluginState);
//        }
        
//    }
//}
