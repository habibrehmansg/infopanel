using Microsoft.Win32;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace InfoPanel.Utils
{
    /// <summary>
    /// Helper class for detecting and installing PawniO driver.
    /// PawniO is a kernel-mode driver that provides low-level hardware access for LibreHardwareMonitor.
    /// </summary>
    public static class PawnIoHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(PawnIoHelper));
        private static readonly Version MinimumVersion = new(2, 0, 0, 0);

        /// <summary>
        /// Gets a value indicating whether PawniO is installed on the system.
        /// </summary>
        public static bool IsInstalled => Version is not null;

        /// <summary>
        /// Gets the installed version of PawniO, or null if not installed.
        /// </summary>
        public static Version? Version { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the installed PawniO requires an update.
        /// </summary>
        public static bool RequiresUpdate => IsInstalled && Version < MinimumVersion;

        /// <summary>
        /// Gets a user-friendly status message about PawniO installation.
        /// </summary>
        public static string StatusMessage
        {
            get
            {
                if (!IsInstalled)
                    return "Not installed";
                if (RequiresUpdate)
                    return $"Outdated (v{Version}, requires v{MinimumVersion} or higher)";
                return $"Installed (v{Version})";
            }
        }

        static PawnIoHelper()
        {
            RefreshStatus();
        }

        /// <summary>
        /// Refreshes the PawniO installation status by checking the registry.
        /// </summary>
        public static void RefreshStatus()
        {
            Version = null;

            try
            {
                // Check standard registry location
                using var subKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (subKey?.GetValue("DisplayVersion") is string versionString && System.Version.TryParse(versionString, out var version))
                {
                    Version = version;
                    Logger.Information("PawniO detected: version {Version}", version);
                    return;
                }

                // Check WOW64 registry location (for 32-bit apps on 64-bit Windows)
                using var registryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var subKeyWow64 = registryKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (subKeyWow64?.GetValue("DisplayVersion") is string versionStringWow64 && System.Version.TryParse(versionStringWow64, out var versionWow64))
                {
                    Version = versionWow64;
                    Logger.Information("PawniO detected (WOW64): version {Version}", versionWow64);
                    return;
                }

                Logger.Information("PawniO not detected");
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to check PawniO installation status");
            }
        }

        /// <summary>
        /// Installs or updates PawniO by extracting and running the embedded installer.
        /// </summary>
        /// <returns>True if installation was successful or user cancelled, false if an error occurred.</returns>
        public static bool InstallOrUpdate()
        {
            try
            {
                Logger.Information("Starting PawniO installation/update");

                string? installerPath = ExtractInstaller();
                if (string.IsNullOrEmpty(installerPath))
                {
                    Logger.Error("Failed to extract PawniO installer");
                    return false;
                }

                try
                {
                    Logger.Debug("Launching PawniO installer: {Path}", installerPath);
                    var process = Process.Start(new ProcessStartInfo(installerPath, "-install")
                    {
                        UseShellExecute = true,
                        Verb = "runas" // Request admin elevation
                    });

                    if (process == null)
                    {
                        Logger.Warning("Failed to start PawniO installer process");
                        return false;
                    }

                    process.WaitForExit();
                    Logger.Information("PawniO installer exited with code: {ExitCode}", process.ExitCode);

                    // Refresh status after installation
                    RefreshStatus();

                    return true;
                }
                finally
                {
                    // Clean up installer file
                    try
                    {
                        if (File.Exists(installerPath))
                        {
                            File.Delete(installerPath);
                            Logger.Debug("Deleted temporary installer: {Path}", installerPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Failed to delete temporary installer file: {Path}", installerPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to install/update PawniO");
                return false;
            }
        }

        /// <summary>
        /// Extracts the embedded PawniO installer to a temporary location.
        /// </summary>
        /// <returns>Path to the extracted installer, or null if extraction failed.</returns>
        private static string? ExtractInstaller()
        {
            string destination = Path.Combine(Path.GetTempPath(), $"PawnIO_setup_{Guid.NewGuid()}.exe");

            try
            {
                using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("InfoPanel.Resources.PawnIO_setup.exe");
                if (resourceStream == null)
                {
                    Logger.Error("PawnIO_setup.exe resource not found in assembly");
                    return null;
                }

                using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write);
                resourceStream.CopyTo(fileStream);

                Logger.Debug("Extracted PawniO installer to: {Path}", destination);
                return destination;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to extract PawniO installer");
                return null;
            }
        }
    }
}
