﻿using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System.Collections.ObjectModel;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public class UpdatesViewModel : ObservableObject
    {
        public string Version { get; set; }

        public VersionModel? VersionModel { get; set; }

        private bool _updateCheckInProgress = false;

        public bool UpdateCheckInProgress
        {
            get { return _updateCheckInProgress; }
            set { SetProperty(ref _updateCheckInProgress, value); }
        }

        private bool _downloadInProgress = false;
        public bool DownloadInProgress
        {
            get { return _downloadInProgress; }
            set { SetProperty(ref _downloadInProgress, value); }
        }

        private double _downloadProgress = 0;

        public double DownloadProgress
        {
            get { return _downloadProgress; }
            set { SetProperty(ref _downloadProgress, value); }
        }

        private bool _updateAvailable = false;
        public bool UpdateAvailable
        {
            get { return _updateAvailable; }
            set { SetProperty(ref _updateAvailable, value); }
        }

        public ObservableCollection<UpdateVersion> UpdateVersions { get; } = [];

        public UpdatesViewModel()
        {
            Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);

            var version128 = new UpdateVersion()
            {
                Version = "v1.2.8",
                Title = "Performance & Feature Updates",
                Items = [
                     new UpdateVersionItem() { Title = "Direct2D GPU Acceleration",
                    Description = [
                        "Introduced a new GPU-accelerated rendering profile option for desktop panels, offering superior FPS with minimal CPU usage, even at higher resolutions.",
                        "Backward compatible with non-accelerated profiles, with offset settings provided.",
                        "Graphs and charts are animated via interpolation in this mode."
                        ] },
                    new UpdateVersionItem() { Title = "Libre Sensors",
                    Description = [
                        "Introduced a new built-in method to query PC sensors, powered by LibreHardwareMonitor.",
                        "Fully compatible with HWiNFO sensors and can be used interchangeably."
                        ] },
                    new UpdateVersionItem() { Title = "Enhanced USB LCD Support",
                    Description = [
                        "Added support for Turing (Turzx) 8.8\" LCD.",
                        "Improved LCD stability and performance."
                        ] },
                    new UpdateVersionItem() { Title = "Donut Chart",
                    Description = [
                        "Introduced a new customizable circular chart design option, previously available only as a user-provided custom gauge."
                        ] },
                    new UpdateVersionItem() { Title = "Sensor Image",
                    Description = [
                        "Added support for images to show/hide based on sensor value range."
                        ] },
                    new UpdateVersionItem() { Title = "UI Improvements",
                    Description = [
                        "Optimized and enabled resizing with a responsive layout for InfoPanel, enhancing support for low resolutions.",
                        "Added chart horizontal flip option.",
                        "Introduced GIF image preview support.",
                        "Resolved several UI bugs."
                        ] },
                    new UpdateVersionItem() { Title = "Performance Enhancements",
                    Description = [
                        "Updated autostart setting to use Windows Task Scheduler instead of the registry run key.",
                        "Reduced CPU and memory usage for non-accelerated profiles through smart caching.",
                        "Resolved several memory-related issues."
                        ] }
                ]
            };

            var version130 =
                new UpdateVersion
                {
                    Version = "v1.3.0",
                    Expanded = true,
                    Title = "Video streaming, multiple panel support, and new design tools",
                    Items = [
                            new UpdateVersionItem() { Title = "Videos & Live Streams",
                            Description = [
                                "Play videos and live streams with full audio support.",
                                "Perfect for security cameras, media players, or any video content.",
                                "Volume control and professional video playback."
                                ] },
                            new UpdateVersionItem() { Title = "Multiple USB Panel Support",
                            Description = [
                                "Connect multiple BeadaPanel AND Turing displays simultaneously.",
                                "Support for latest Turing 8.8\" Rev 1.1 models.",
                                "Each panel works independently with automatic detection."
                                ] },
                            new UpdateVersionItem() { Title = "Creative Design Tools",
                            Description = [
                                "Custom shapes with animated gradients and rounded bar charts.",
                                "SVG support for crisp icons and a design grid for alignment.",
                                "Animated live bars for dynamic visualizations."
                                ] },
                            new UpdateVersionItem() { Title = "Smart Text & Organization",
                            Description = [
                                "Scrolling marquee text, wrapping, and ellipsis options.",
                                "Group items together, lock them, and clone entire groups.",
                                "Search to find items quickly and drag & drop positioning."
                                ] },
                            new UpdateVersionItem() { Title = "Modern Windows 11 Interface",
                            Description = [
                                "Fresh new look with system tray support.",
                                "Window size saves automatically.",
                                "Consistent font scaling across all profiles."
                                ] },
                            new UpdateVersionItem() { Title = "Performance & Reliability",
                            Description = [
                                "New high-performance graphics engine for smoother animations.",
                                "Auto-start delay option and better plugin support.",
                                "Installer preserves your settings and plugin configurations.",
                                "Updated LibreHardwareMonitor to resolve WinRing0 compatibility issues."
                                ] }
                            ]
                };

            var version129 =
                new UpdateVersion
                {
                    Version = "v1.2.9",
                    Expanded = false,
                    Title = "Plugins, additional features and bug fixes.",
                    Items = [
                            new UpdateVersionItem() { Title = "Plugin Support",
                            Description = [
                                "Introduced new plugin support, enabling developers to create custom sensors outside of InfoPanel. Includes two new sensors: HTTP Image & Table View.",
                                "HTTP Image allows plugins to load and dynamically update images from URLs.",
                                "Table View supports advanced custom rendering of rows and columns, ideal for items like Top 10 Processes.",
                                "Includes bundled InfoPanel plugins for features beyond HwInfo & Libre."
                                ] },
                            new UpdateVersionItem() { Title = "Video Background",
                            Description = [
                                "Introduced high-resolution video background support for desktop profiles.",
                                "Hardware acceleration ensures low CPU usage, regardless of Direct2D mode."
                                ] },
                            new UpdateVersionItem() { Title = "Sensor Updates",
                            Description = [
                                "Added width option to text items for length limitation and auto-wrapping.",
                                "Introduced center alignment option for text items.",
                                "Added width/height overrides for images and gauges.",
                                "Added division (or existing multiplication) support to sensors.",
                                "Introduced span support to donut charts for a more customized appearance.",
                                "Added support for static and animated WebP formats in images.",
                                "Introduced cache option for images to enhance performance."
                                ] },
                            new UpdateVersionItem() { Title = "USB Panel Updates",
                            Description = [
                                "Enabled USB panels to turn off during system shutdown.",
                                "Improved detection and support for Turing (Turzx) panels."
                                ] },
                            new UpdateVersionItem() { Title = "SensorPanel Import",
                            Description = [
                                "Added import support for .sensorpanel files.",
                                "This feature is experimental and not officially supported by or affiliated with Aida64.",
                                "Aida64 is a registered trademark of FinalWire Ltd. This project is not endorsed by or related to FinalWire Ltd."
                                ] },
                            new UpdateVersionItem() { Title = "Bug Fixes",
                            Description = [
                                "Resolved GIF rendering issue for GIFs utilizing advanced drawing options."
                                ] },
                            ]
                };

            UpdateVersions.Add(version130);
            UpdateVersions.Add(version129);
            UpdateVersions.Add(version128);
        }
    }

    public class UpdateVersion()
    {
        public required string Version { get; set; }
        public required string Title { get; set; }
        public bool Expanded { get; set; } = false;
        public required ObservableCollection<UpdateVersionItem> Items { get; set; }
    }

    public class UpdateVersionItem()
    {
        public required string Title { get; set; }
        public required ObservableCollection<string> Description { get; set; }
    }

}
