using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System.Collections.ObjectModel;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public class AboutViewModel: ObservableObject
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

        public ObservableCollection<InfoLink> InfoLinks { get; } = [];
        public ObservableCollection<ThirdPartyLicense> ThirdPartyLicenses { get; } = [];
        public ObservableCollection<Contributor> Contributors { get; } = [];

        public AboutViewModel() 
        {
            Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
            InitializeCollections();
        }

        private void InitializeCollections()
        {
            // Initialize info links
            InfoLinks.Add(new InfoLink
            {
                Icon = "WebAsset20",
                Title = "Website",
                Description = "https://infopanel.net",
                ButtonText = "Launch",
                NavigateUri = "https://infopanel.net/"
            });

            InfoLinks.Add(new InfoLink
            {
                Icon = "HardDrive20",
                Title = "HWiNFO",
                Description = "Download HWiNFO to get started if you have not.",
                ButtonText = "Download",
                NavigateUri = "https://www.hwinfo.com/"
            });

            InfoLinks.Add(new InfoLink
            {
                Icon = "Chat20",
                Title = "Discord",
                Description = "Join in conversations with others regarding InfoPanel.",
                ButtonText = "Join",
                NavigateUri = "https://discord.gg/cQnjdMC7Qc"
            });

            InfoLinks.Add(new InfoLink
            {
                Icon = "WebAsset20",
                Title = "Reddit",
                Description = "Help grow the /r/InfoPanel community.",
                ButtonText = "Launch",
                NavigateUri = "https://www.reddit.com/r/InfoPanel/"
            });

            InfoLinks.Add(new InfoLink
            {
                Icon = "Heart20",
                Title = "Love InfoPanel?",
                Description = "Leave a review for others to see and help InfoPanel grow.",
                ButtonText = "Review",
                NavigateUri = "ms-windows-store://review/?ProductId=XPFP7C8H5446ZD"
            });

            InfoLinks.Add(new InfoLink
            {
                Icon = "DrinkCoffee20",
                Title = "Support Development",
                Description = "Show appreciation and help to offset costs incurred such web and certificate fees.",
                ButtonText = "Donate",
                NavigateUri = "https://www.buymeacoffee.com/urfath3r"
            });

            // Initialize third-party licenses
            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "HWiNFO",
                License = "Licensed software. © Martin Malik, REALiX, s.r.o.",
                ProjectUrl = "https://www.hwinfo.com/"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "LibreHardwareMonitor",
                License = "Mozilla Public License Version 2.0",
                ProjectUrl = "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "WinUSBNet",
                License = "MIT License. Copyright © 2010 Thomas Bleeker.",
                ProjectUrl = "https://github.com/madwizard-thomas/winusbnet"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "TuringSmartScreenLib",
                License = "MIT License. Copyright © 2021 machi_pon.",
                ProjectUrl = "https://github.com/usausa/turing-smart-screen"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "MahApps.Metro",
                License = "MIT License. Copyright © .NET Foundation, Jan Karger, Brendan Forster, Dennis Daume, Alex Mitchell, Paul Jenkins and contributors.",
                ProjectUrl = "https://github.com/MahApps/MahApps.Metro"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "WPF UI",
                License = "MIT License. Copyright © 2021-2023 Leszek Pomianowski and WPF UI Contributors.",
                ProjectUrl = "https://github.com/lepoco/wpfui"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "AutoMapper",
                License = "MIT License. Copyright © 2010 Jimmy Bogard.",
                ProjectUrl = "https://github.com/AutoMapper/AutoMapper"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "BouncyCastle.NetCore",
                License = "MIT X Consortium License. Copyright © The Legion of the Bouncy Castle.",
                ProjectUrl = "https://www.bouncycastle.org/"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "CommunityToolkit.Mvvm",
                License = "MIT License. Copyright © .NET Foundation and Contributors.",
                ProjectUrl = "https://github.com/CommunityToolkit/dotnet"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "Flurl.Http",
                License = "MIT License. Copyright © 2023 Todd Menier.",
                ProjectUrl = "https://github.com/tmenier/Flurl"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "FlyleafLib",
                License = "LGPL-3.0-or-later. Copyright © SuRGeoNix.",
                ProjectUrl = "https://github.com/SuRGeoNix/Flyleaf"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "gong-wpf-dragdrop",
                License = "BSD 3-Clause License. Copyright © 2015-2016 Jan Karger, Bastian Schmidt, Steven Kirk.",
                ProjectUrl = "https://github.com/punker76/gong-wpf-dragdrop"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "HidSharp",
                License = "Apache License 2.0. Copyright © 2012 James F. Bellinger.",
                ProjectUrl = "https://www.zer7.com/software/hidsharp"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "ini-parser-netstandard",
                License = "MIT License. Copyright © 2008 Ricardo Amores Hernández.",
                ProjectUrl = "https://github.com/rickyah/ini-parser"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "LibUsbDotNet",
                License = "LGPL v2 / GPL v2 (dual licensed). Copyright © LibUsbDotNet Contributors.",
                ProjectUrl = "https://github.com/LibUsbDotNet/LibUsbDotNet"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "Microsoft.Extensions.*",
                License = "MIT License. Copyright © Microsoft Corporation.",
                ProjectUrl = "https://github.com/dotnet/runtime"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "SecureStore",
                License = "MIT License. Copyright © 2016 Dmitry Lokshin.",
                ProjectUrl = "https://github.com/dscoduc/SecureStore"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "Sentry",
                License = "MIT License. Copyright © 2021 Sentry.",
                ProjectUrl = "https://github.com/getsentry/sentry-dotnet"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "Serilog",
                License = "Apache License 2.0. Copyright © Serilog Contributors.",
                ProjectUrl = "https://github.com/serilog/serilog"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "SkiaSharp",
                License = "MIT License. Copyright © 2015-2016 Xamarin, Inc., Microsoft Corporation.",
                ProjectUrl = "https://github.com/mono/SkiaSharp"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "Svg.Skia",
                License = "MIT License. Copyright © Wiesław Šoltés.",
                ProjectUrl = "https://github.com/wieslawsoltes/Svg.Skia"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "System.IO.Ports",
                License = "MIT License. Copyright © Microsoft Corporation.",
                ProjectUrl = "https://github.com/dotnet/runtime"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "System.Management",
                License = "MIT License. Copyright © Microsoft Corporation.",
                ProjectUrl = "https://github.com/dotnet/runtime"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "TaskScheduler",
                License = "MIT License. Copyright © David Hall.",
                ProjectUrl = "https://github.com/dahall/TaskScheduler"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "NAudio.Wasapi",
                License = "MIT License. Copyright © 2020 Mark Heath & Contributors.",
                ProjectUrl = "https://github.com/naudio/NAudio"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "OpenWeatherMap.Standard",
                License = "MIT License. Copyright © Ken Tucker.",
                ProjectUrl = "https://github.com/Ken-Tucker/OpenWeatherMap.Standard"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense
            {
                Name = "FFmpeg",
                License = "GPL 2+. Copyright © FFmpeg developers.",
                ProjectUrl = "https://ffmpeg.org/"
            });

            // Initialize contributors
            Contributors.Add(new Contributor
            {
                Name = "F3NN3X",
                Description = "For the countless support and awesome plugins."
            });

            Contributors.Add(new Contributor
            {
                Name = "/u/ME5ER",
                Description = "Special thanks for patiently troubleshooting the early and buggy software iterations over extended periods."
            });

            Contributors.Add(new Contributor
            {
                Name = "/u/DRA6N",
                Description = "Better known as RobOnTwoWheels our CM on Discord, without whom it would not have existed."
            });

            Contributors.Add(new Contributor
            {
                Name = "Boredape",
                Description = "For your graphical musings."
            });

            Contributors.Add(new Contributor
            {
                Name = "Everyone else",
                Description = "For those that messaged me or posted your questions, feedback and panel designs on Reddit, HWiNFO forums and Discord."
            });
        }
    }

    public class InfoLink
    {
        public required string Icon { get; set; }
        public required string Title { get; set; }
        public required string Description { get; set; }
        public required string ButtonText { get; set; }
        public required string NavigateUri { get; set; }
    }

    public class ThirdPartyLicense
    {
        public required string Name { get; set; }
        public required string License { get; set; }
        public required string ProjectUrl { get; set; }
    }

    public class Contributor
    {
        public required string Name { get; set; }
        public required string Description { get; set; }
    }
}
