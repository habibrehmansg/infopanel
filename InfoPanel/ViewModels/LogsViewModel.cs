using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace InfoPanel.ViewModels
{
    public partial class LogsViewModel : ObservableObject
    {
        private static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "logs");

        private string _logText = string.Empty;
        private bool _hasLogs;

        public string LogText
        {
            get => _logText;
            set => SetProperty(ref _logText, value);
        }

        public bool HasLogs
        {
            get => _hasLogs;
            set => SetProperty(ref _hasLogs, value);
        }

        [RelayCommand]
        private void LoadLogs()
        {
            try
            {
                var logFile = Directory.EnumerateFiles(LogDirectory, "infopanel-*.log")
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();

                if (logFile == null)
                {
                    LogText = "No log file found.";
                    return;
                }

                var sessionStart = new DateTimeOffset(Process.GetCurrentProcess().StartTime);
                var sb = new StringBuilder();
                bool inSession = false;

                using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Log lines start with "yyyy-MM-dd HH:mm:ss.fff zzz" (29 chars)
                    // Non-timestamp lines (e.g. exception stack traces) inherit the current session state
                    if (line.Length >= 29 && DateTimeOffset.TryParse(line[..29], out var lineTime))
                    {
                        inSession = lineTime >= sessionStart;
                    }

                    if (inSession)
                    {
                        sb.AppendLine(line);
                    }
                }

                LogText = sb.ToString();
                HasLogs = LogText.Length > 0;
            }
            catch (Exception ex)
            {
                LogText = $"Failed to read log file: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CopyToClipboard()
        {
            if (!string.IsNullOrEmpty(LogText))
            {
                Clipboard.SetText(LogText);
            }
        }
    }
}
