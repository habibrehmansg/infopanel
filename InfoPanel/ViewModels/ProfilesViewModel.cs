using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using InfoPanel.Utils;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Wpf.Ui.Abstractions.Controls;

namespace InfoPanel.ViewModels
{
    /// <summary>
    /// One connected monitor, for the "assign profile to display" dropdown.
    /// </summary>
    public class DisplayOption
    {
        public string DeviceName { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public bool IsPrimary { get; init; }

        /// <summary>Monitor name from EDID (what Windows Settings shows), e.g. "LG ULTRAWIDE". May be empty.</summary>
        public string FriendlyName { get; init; } = "";

        /// <summary>
        /// e.g. "LG ULTRAWIDE  3440x1440 @ 0,0  (primary)  [DISPLAY2]". Leads with the friendly
        /// name because the GDI "DISPLAYn" number does not match the Display 1/2 numbering in
        /// Windows Settings.
        /// </summary>
        public string Label
        {
            get
            {
                var gdi = DeviceName[(DeviceName.LastIndexOf(System.IO.Path.DirectorySeparatorChar) + 1)..]; // strip the \.\ prefix
                var head = string.IsNullOrEmpty(FriendlyName) ? gdi : FriendlyName;
                var tail = string.IsNullOrEmpty(FriendlyName) ? "" : $"  [{gdi}]";
                return $"{head}  {Width}x{Height} @ {X},{Y}{(IsPrimary ? "  (primary)" : "")}{tail}";
            }
        }

        public TargetWindow ToTargetWindow() => new(X, Y, Width, Height, DeviceName);

        public override string ToString() => Label;
    }

    public class ProfilesViewModel: ObservableObject, INavigationAware
    {
        private Profile? _profile;

        public Profile? Profile
        {
            get { return _profile; }
            set
            {
                SetProperty(ref _profile, value);
            }
        }

        public ObservableCollection<DisplayOption> Displays { get; } = [];

        public ProfilesViewModel()
        {
            RefreshDisplays();
        }

        /// <summary>Re-enumerates connected monitors (called on navigation so hot-plugged screens appear).</summary>
        public void RefreshDisplays()
        {
            var friendly = DisplayNameHelper.GetFriendlyNames();
            var monitors = ScreenHelper.GetAllMonitors()
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.DeviceName)
                .Select(m => new DisplayOption
                {
                    DeviceName = m.DeviceName ?? "",
                    FriendlyName = (m.DeviceName != null && friendly.TryGetValue(m.DeviceName, out var fn)) ? fn : "",
                    X = (int)m.Bounds.Left,
                    Y = (int)m.Bounds.Top,
                    Width = (int)m.Bounds.Width,
                    Height = (int)m.Bounds.Height,
                    IsPrimary = m.IsPrimary,
                })
                .ToList();

            Displays.Clear();
            foreach (var m in monitors) Displays.Add(m);
        }

        /// <summary>
        /// Finds the dropdown entry matching a profile's current TargetWindow by device name
        /// (falls back to geometry if the name is missing), or null if that display is not connected.
        /// </summary>
        public DisplayOption? FindDisplayFor(TargetWindow? target)
        {
            if (target == null) return null;
            if (!string.IsNullOrEmpty(target.DeviceName))
            {
                var byName = Displays.FirstOrDefault(d => string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
            }
            return Displays.FirstOrDefault(d => d.X == target.X && d.Y == target.Y && d.Width == target.Width && d.Height == target.Height);
        }

        public Task OnNavigatedFromAsync()
        {
            return Task.CompletedTask;
        }

        public Task OnNavigatedToAsync()
        {
            RefreshDisplays();
            return Task.CompletedTask;
        }
    }
}
