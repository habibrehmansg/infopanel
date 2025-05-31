using InfoPanel.Models;
using InfoPanel.Views.Common;
using System;
using System.Windows.Threading;
using System.Windows;
using System.Threading;

namespace InfoPanel
{
    public class DisplayWindowThread
    {
        private readonly Profile _profile;
        private Thread? _thread;
        private DisplayWindow? _window;
        private Dispatcher? _dispatcher;
        private readonly ManualResetEventSlim _readyEvent = new();

        public DisplayWindow? Window => _window;

        public bool Direct2DMode;
        public event EventHandler<Guid>? WindowClosed;

        public DisplayWindowThread(Profile profile)
        {
            _profile = profile;
            Direct2DMode = profile.Direct2DMode;
        }

        public void Start()
        {
            _thread = new Thread(ThreadMain)
            {
                IsBackground = false
            };

            // Call SetApartmentState as a method before starting
            _thread.SetApartmentState(ApartmentState.STA);
            
            _thread.Start();
            _readyEvent.Wait(5000); // Wait for window to be ready
        }

        private void ThreadMain()
        {
            _window = new DisplayWindow(_profile);
            _dispatcher = _window.Dispatcher;

            _window.Closed += (s, e) =>
            {
                WindowClosed?.Invoke(this, _profile.Guid);
                _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };

            _readyEvent.Set();
            _window.Show();

            // Start the message pump without Application
            Dispatcher.Run();
        }

        public void Show()
        {
            _dispatcher?.BeginInvoke(() => _window?.Show());
        }

        public void Close()
        {
            _dispatcher?.BeginInvoke(() => _window?.Close());
            _thread?.Join(2000);
        }

        public void UpdateProfile(Profile profile)
        {
            //_dispatcher?.BeginInvoke(() => _window?.UpdateProfile(profile));
        }
    }
}
