using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using InfoPanel.TuringPanel;
using InfoPanel.Views.Windows;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;

namespace InfoPanel.ViewModels;

public partial class TuringDeviceWindowViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<TuringDeviceWindowViewModel>();
    private readonly TuringPanelDevice _device;
    private TuringDevice? _turingDevice;

    [ObservableProperty]
    private string _deviceName = "Turing Device";

    [ObservableProperty]
    private string _deviceId = "";

    [ObservableProperty]
    private string _deviceStatus = "Disconnected";

    [ObservableProperty]
    private string _firmwareVersion = "Unknown";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingText = "Loading...";

    // Storage properties
    [ObservableProperty]
    private string _totalStorageDisplay = "0 MB";

    [ObservableProperty]
    private string _usedStorageDisplay = "0 MB";

    [ObservableProperty]
    private string _freeStorageDisplay = "0 MB";

    [ObservableProperty]
    private double _storageUsagePercentage;

    [ObservableProperty]
    private ObservableCollection<DeviceFile> _deviceFiles = new();

    [ObservableProperty]
    private DeviceFile? _selectedFile;

    [ObservableProperty]
    private bool _isFileSelected;

    // Playback properties
    [ObservableProperty]
    private string _currentlyPlaying = "Nothing";

    // Configuration properties
    [ObservableProperty]
    private int _brightness = 50;

    [ObservableProperty]
    private List<string> _orientationOptions = new() { "0°", "180°" };

    [ObservableProperty]
    private string _selectedOrientation = "0°";

    [ObservableProperty]
    private List<string> _startupModeOptions = new() { "None", "Video" };

    [ObservableProperty]
    private string _selectedStartupMode = "None";

    // Status properties
    [ObservableProperty]
    private bool _isStatusVisible;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private string _statusTitle = "";

    [ObservableProperty]
    private string _statusMessage = "";

    public TuringDeviceWindowViewModel(TuringPanelDevice device)
    {
        _device = device;
        DeviceId = device.DeviceId ?? "Unknown";
        DeviceName = $"Turing Device - {DeviceId}";
        Brightness = device.Brightness;
        SelectedOrientation = device.Rotation switch
        {
            LCD_ROTATION.Rotate180FlipNone => "180°",
            _ => "0°"
        };

        Task.Run(InitializeDevice);
    }

    private async Task<UsbRegistry?> FindTargetDeviceAsync()
    {
        foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
        {
            if (deviceReg.Vid == 0x1cbe && deviceReg.Pid == 0x0088) // VENDOR_ID and PRODUCT_ID from TuringDevice
            {
                var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;

                if (string.IsNullOrEmpty(deviceId))
                {
                    Logger.Debug("TuringPanelDevice {Device}: Unable to get DeviceId for device {DevicePath}", _device, deviceReg.DevicePath);
                    continue;
                }

                if (_device.IsMatching(deviceId))
                {
                    Logger.Information("TuringPanelDevice {Device}: Found matching device with DeviceId {DeviceId}", _device, deviceId);
                    return deviceReg;
                }
            }
        }

        return null;
    }

    private async Task InitializeDevice()
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsLoading = true;
                LoadingText = "Connecting to device...";
            });

            var usbRegistry = await FindTargetDeviceAsync();

            if (usbRegistry == null)
            {
                Logger.Warning("TuringPanelDevice {Device}: USB Device not found.", _device);
                _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                return;
            }

            _turingDevice = new TuringDevice();
            
            try
            {
                _turingDevice.Initialize(usbRegistry);
                Application.Current.Dispatcher.Invoke(() => DeviceStatus = "Connected");
                await RefreshStorage();
            }
            catch (TuringDeviceException ex)
            {
                Application.Current.Dispatcher.Invoke(() => DeviceStatus = "Failed to connect");
                ShowStatus("Connection Failed", $"Could not connect to the Turing device: {ex.Message}", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => DeviceStatus = "Error");
            ShowStatus("Error", $"Failed to initialize device: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(() => IsLoading = false);
        }
    }

    partial void OnSelectedFileChanged(DeviceFile? value)
    {
        IsFileSelected = value != null;
    }

    [RelayCommand]
    private async Task RefreshStorage()
    {
        if (_turingDevice == null) return;

        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsLoading = true;
                LoadingText = "Refreshing storage information...";
            });

            // Get storage info
            try
            {
                var storageInfo = _turingDevice.GetStorageInfo();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    TotalStorageDisplay = FormatBytes(storageInfo.TotalBytes);
                    UsedStorageDisplay = FormatBytes(storageInfo.UsedBytes);
                    FreeStorageDisplay = FormatBytes(storageInfo.TotalBytes - storageInfo.UsedBytes);
                    StorageUsagePercentage = storageInfo.TotalBytes > 0 
                        ? (double)storageInfo.UsedBytes / storageInfo.TotalBytes * 100 
                        : 0;
                });
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to get storage info");
            }

            // Get video file list
            Application.Current.Dispatcher.Invoke(() => LoadingText = "Loading video files...");
            
            try
            {
                var files = _turingDevice.ListFiles("/tmp/sdcard/mmcblk0p1/video/");
                
                // Update collection on UI thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    DeviceFiles.Clear();
                    foreach (var fileName in files)
                    {
                        DeviceFiles.Add(new DeviceFile
                        {
                            Name = fileName,
                            Type = "Video",
                            Size = 0, // Size not provided by ListFiles
                            SizeDisplay = "N/A"
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to get file list");
            }

            ShowStatus("Success", "Storage information refreshed successfully.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus("Error", $"Failed to refresh storage: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(() => IsLoading = false);
        }
    }

    [RelayCommand]
    private async Task UploadVideo()
    {
        if (_turingDevice == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select Video File",
            Filter = "MP4 Files|*.mp4|All Files|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                IsLoading = true;
                LoadingText = "Uploading video...";

                var fileName = Path.GetFileName(dialog.FileName);

                try
                {
                    await Task.Run(() => { 
                        _turingDevice.UploadFile(dialog.FileName);
                    });

                    ShowStatus("Success", $"Video '{fileName}' uploaded successfully.", InfoBarSeverity.Success);
                    await RefreshStorage();
                }
                catch (TuringDeviceException ex)
                {
                    ShowStatus("Upload Failed", $"Failed to upload video '{fileName}': {ex.Message}", InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                ShowStatus("Error", $"Upload error: {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }


    [RelayCommand]
    private async Task PlayFile(DeviceFile? file)
    {
        if (_turingDevice == null || file == null) return;

        try
        {
            IsLoading = true;
            LoadingText = $"Playing {file.Name}...";

            // Always stop current playback first
            try
            {
                _turingDevice.StopPlay();
                _turingDevice.SendSyncCommand();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to stop playback: {ex.Message}");
            }

            await Task.Delay(200);

            // Clear the screen
            try
            {
                _turingDevice.ClearScreen();
                _turingDevice.SendBrightnessCommand(100);
                _turingDevice.SendSyncCommand();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to clear screen: {ex.Message}");
            }

            // Small delay to ensure commands are processed
            await Task.Delay(200);

            // Play video file from device storage
            try
            {
                _turingDevice.PlayFile(file.Name);
                _turingDevice.SendSyncCommand();

                await Task.Delay(200);

                CurrentlyPlaying = file.Name;
                ShowStatus("Playing", $"Now playing: {file.Name}", InfoBarSeverity.Success);
            }
            catch (TuringDeviceException ex)
            {
                CurrentlyPlaying = "Nothing";
                ShowStatus("Playback Failed", $"Failed to play {file.Name}: {ex.Message}", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            CurrentlyPlaying = "Nothing";
            ShowStatus("Error", $"Playback error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteFile(DeviceFile? file)
    {
        if (_turingDevice == null || file == null) return;

        var result = System.Windows.MessageBox.Show(
            $"Are you sure you want to delete '{file.Name}'?",
            "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            try
            {
                IsLoading = true;
                LoadingText = $"Deleting {file.Name}...";

                // If this is the currently playing file, stop it first
                if (CurrentlyPlaying == file.Name)
                {
                    _turingDevice.StopPlay();
                    CurrentlyPlaying = "Nothing";
                }

                try
                {
                    _turingDevice.DeleteFile(file.Name);
                    ShowStatus("Success", $"File '{file.Name}' deleted successfully.", InfoBarSeverity.Success);
                    await RefreshStorage();
                }
                catch (TuringDeviceException ex)
                {
                    ShowStatus("Delete Failed", $"Failed to delete '{file.Name}': {ex.Message}", InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                ShowStatus("Error", $"Delete error: {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }



    [RelayCommand]
    private async Task SaveSettings()
    {
        if (_turingDevice == null) return;

        try
        {
            IsLoading = true;
            LoadingText = "Saving settings...";

            // Determine startup mode value
            byte startupMode = SelectedStartupMode == "Video" ? (byte)2 : (byte)0;
            
            // Determine rotation value (0 or 2)
            byte rotation = SelectedOrientation == "180°" ? (byte)2 : (byte)0;

            // Save all settings to device
            try
            {
                _turingDevice.SendSaveSettingsCommand(
                    brightness: (byte)Brightness,
                    startup: startupMode,
                    rotation: rotation
                );

                string startupModeText = startupMode == 2 && !string.IsNullOrEmpty(CurrentlyPlaying) && CurrentlyPlaying != "Nothing"
                    ? $"Video mode with '{CurrentlyPlaying}' set as auto-play"
                    : "None";
                    
                ShowStatus("Success", $"Settings saved. Brightness: {Brightness}%, Orientation: {SelectedOrientation}, Startup: {startupModeText}", InfoBarSeverity.Success);
            }
            catch (TuringDeviceException ex)
            {
                ShowStatus("Save Failed", $"Failed to save settings to device: {ex.Message}", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("Error", $"Failed to save settings: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RestartDevice()
    {
        if (_turingDevice == null) return;

        var result = MessageBox.Show(
            "Are you sure you want to restart the device?",
            "Confirm Restart",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            try
            {
                IsLoading = true;
                LoadingText = "Restarting device...";

                try
                {
                    _turingDevice.SendRestartDeviceCommand();
                    ShowStatus("Success", "Device restart command sent.", InfoBarSeverity.Success);
                    
                    // Close window after restart
                    await Task.Delay(2000);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var window = Application.Current.Windows.OfType<TuringDeviceWindow>().FirstOrDefault();
                        window?.Close();
                    });
                }
                catch (TuringDeviceException ex)
                {
                    ShowStatus("Error", $"Failed to restart device: {ex.Message}", InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                ShowStatus("Error", $"Failed to restart device: {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }


    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusTitle = title;
            StatusMessage = message;
            StatusSeverity = severity;
            IsStatusVisible = true;
        });

        // Auto-hide after 5 seconds
        Task.Delay(5000).ContinueWith(_ => 
        {
            Application.Current.Dispatcher.Invoke(() => IsStatusVisible = false);
        }, TaskScheduler.Default);
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private string GetFileType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLower();
        return extension switch
        {
            ".mp4" or ".h264" => "Video",
            _ => "Other"
        };
    }

    public void Cleanup()
    {
        _turingDevice?.Dispose();
    }
}

public partial class DeviceFile : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _type = "";

    [ObservableProperty]
    private long _size;

    [ObservableProperty]
    private string _sizeDisplay = "";
}