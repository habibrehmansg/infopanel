using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Logging;
using System;
using System.Windows;
using System.Windows.Threading;

namespace InfoPanel.ViewModels
{
    public partial class LogsViewModel : ObservableObject
    {
        private string _logText = string.Empty;
        private readonly Dispatcher _dispatcher;

        public string LogText
        {
            get => _logText;
            set => SetProperty(ref _logText, value);
        }

        [RelayCommand]
        private void CopyToClipboard()
        {
            if (!string.IsNullOrEmpty(LogText))
            {
                Clipboard.SetText(LogText);
            }
        }

        public LogsViewModel()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            LogText = InMemoryLogSink.Instance.GetLogs();
            InMemoryLogSink.Instance.LogReceived += OnLogReceived;
        }

        private void OnLogReceived(string line)
        {
            _dispatcher.BeginInvoke(() =>
            {
                LogText = InMemoryLogSink.Instance.GetLogs();
            }, DispatcherPriority.Background);
        }
    }
}
