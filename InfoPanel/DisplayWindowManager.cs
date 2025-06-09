using InfoPanel.Models;
using InfoPanel.Views.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;

namespace InfoPanel
{
    public class DisplayWindowManager
    {
        private static readonly Lazy<DisplayWindowManager> _instance = new(() => new DisplayWindowManager());
        public static DisplayWindowManager Instance => _instance.Value;

        private readonly Dictionary<Guid, DisplayWindow> _windows = [];
        private Thread? _uiThread;
        public Dispatcher? Dispatcher { get; private set; }
        private readonly ManualResetEventSlim _threadReady = new();
        private readonly object _lock = new();

        private DisplayWindowManager()
        {
            StartUIThread();
        }

        private void StartUIThread()
        {
            _uiThread = new Thread(() =>
            {
                // Create dispatcher for this thread
                Dispatcher = Dispatcher.CurrentDispatcher;
                _threadReady.Set();

                // Run the dispatcher
                Dispatcher.Run();
            })
            {
                Name = "DisplayWindowThread",
                IsBackground = false
            };

            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();

            // Wait for thread to be ready
            _threadReady.Wait(5000);
        }

        public void ShowDisplayWindow(Profile profile)
        {
            if (Dispatcher == null) return;

            Dispatcher.BeginInvoke(() =>
            {
                lock (_lock)
                {
                    // Check if window exists
                    if (_windows.TryGetValue(profile.Guid, out var existingWindow))
                    {
                        // If Direct2D mode changed, close and recreate
                        if (existingWindow.Direct2DMode != profile.Direct2DMode)
                        {
                            existingWindow.Close();
                            _windows.Remove(profile.Guid);
                            CreateAndShowWindow(profile);
                        }
                        else
                        {
                            // Just show existing window
                            existingWindow.Show();
                            existingWindow.Activate();
                        }
                    }
                    else
                    {
                        CreateAndShowWindow(profile);
                    }
                }
            });
        }

        private void CreateAndShowWindow(Profile profile)
        {
            var window = new DisplayWindow(profile);
            window.Closed += (s, e) =>
            {
                lock (_lock)
                {
                    _windows.Remove(profile.Guid);

                    // If no more windows, optionally shut down the thread
                    if (_windows.Count == 0 && AllowThreadShutdown)
                    {
                        Dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }
            };

            _windows[profile.Guid] = window;
            window.Show();
        }

        public void CloseDisplayWindow(Guid profileGuid)
        {
            Dispatcher?.BeginInvoke(() =>
            {
                lock (_lock)
                {
                    if (_windows.TryGetValue(profileGuid, out var window))
                    {
                        window.Close();
                        window.Closed -= (s, e) => { }; // Unsubscribe to avoid memory leaks
                        _windows.Remove(profileGuid);
                    }
                }
            });
        }

        public DisplayWindow? GetWindow(Guid profileGuid)
        {
            lock (_lock)
            {
                _windows.TryGetValue(profileGuid, out var window);
                return window;
            }
        }

        public bool IsWindowOpen(Guid profileGuid)
        {
            lock (_lock)
            {
                return _windows.ContainsKey(profileGuid);
            }
        }

        public void CloseAll()
        {
            Dispatcher?.BeginInvoke(() =>
            {
                lock (_lock)
                {
                    foreach (var window in _windows.Values)
                    {
                        window.Close();
                    }
                    _windows.Clear();
                }

                // Shutdown the dispatcher thread
                Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            });

            // Wait for thread to finish
            _uiThread?.Join(5000);
        }

        public void Dispose()
        {
            CloseAll();
        }

        // Optional: Allow thread to shut down when no windows are open
        public bool AllowThreadShutdown { get; set; } = false;
    }
}