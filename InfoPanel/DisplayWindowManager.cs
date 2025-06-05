using InfoPanel.Models;
using InfoPanel.Views.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace InfoPanel
{
    public class DisplayWindowManager
    {
        private readonly Dictionary<Guid, DisplayWindow> _windows = new();
        private Thread? _uiThread;
        private Dispatcher? _dispatcher;
        private readonly ManualResetEventSlim _threadReady = new();
        private readonly object _lock = new();

        public DisplayWindowManager()
        {
            StartUIThread();
        }

        private void StartUIThread()
        {
            _uiThread = new Thread(() =>
            {
                // Create dispatcher for this thread
                _dispatcher = Dispatcher.CurrentDispatcher;
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
            if (_dispatcher == null) return;

            _dispatcher.BeginInvoke(() =>
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
                        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }
            };

            _windows[profile.Guid] = window;
            window.Show();
        }

        public void CloseDisplayWindow(Guid profileGuid)
        {
            _dispatcher?.BeginInvoke(() =>
            {
                lock (_lock)
                {
                    if (_windows.TryGetValue(profileGuid, out var window))
                    {
                        window.Close();
                        _windows.Remove(profileGuid);
                    }
                }
            });
        }

        public void UpdateProfile(Guid profileGuid, Profile profile)
        {
            _dispatcher?.BeginInvoke(() =>
            {
                lock (_lock)
                {
                    if (_windows.TryGetValue(profileGuid, out var window))
                    {
                        // Update the window's profile
                        // window.UpdateProfile(profile);
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
            _dispatcher?.BeginInvoke(() =>
            {
                lock (_lock)
                {
                    foreach (var window in _windows.Values.ToList())
                    {
                        window.Close();
                    }
                    _windows.Clear();
                }

                // Shutdown the dispatcher thread
                _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
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