using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Models
{
    public class Settings : ObservableObject
    {
        private bool _autostart = false;
        public bool AutoStart
        {
            get { return _autostart; }
            set { SetProperty(ref _autostart, value); }
        }

        private bool _startMinimized = false;
        public bool StartMinimized
        {
            get { return _startMinimized; }
            set { SetProperty(ref _startMinimized, value); }
        }

        private bool _minimizeToTray = true;
        public bool MinimizeToTray
        {
            get { return _minimizeToTray; }
            set { SetProperty(ref _minimizeToTray, value); }
        }

        private bool _beadaPanel = false;
        public bool BeadaPanel
        {
            get { return _beadaPanel; }
            set { SetProperty(ref _beadaPanel, value); }
        }

        private Guid _beadaPanelProfile = Guid.Empty;
        public Guid BeadaPanelProfile
        {
            get { return _beadaPanelProfile; }
            set { SetProperty(ref _beadaPanelProfile, value); }
        }

        private LCD_ROTATION _beadaPanelRotation = 0;
        public LCD_ROTATION BeadaPanelRotation
        {
            get { return _beadaPanelRotation; }
            set
            {
                SetProperty(ref _beadaPanelRotation, value);
            }
        }

        private bool _turingPanelA = false;
        public bool TuringPanelA
        {
            get { return _turingPanelA; }
            set { SetProperty(ref _turingPanelA, value); }
        }

        private string _turingPanelAPort = String.Empty;
        public string TuringPanelAPort
        {
            get { return _turingPanelAPort; }
            set { SetProperty(ref _turingPanelAPort, value); }
        }

        private Guid _turingPanelAProfile = Guid.Empty;
        public Guid TuringPanelAProfile
        {
            get { return _turingPanelAProfile; }
            set { SetProperty(ref _turingPanelAProfile, value); }
        }

        private LCD_ROTATION _turingPanelARotation = 0;
        public LCD_ROTATION TuringPanelARotation
        {
            get { return _turingPanelARotation; }
            set
            {
                SetProperty(ref _turingPanelARotation, value);
            }
        }

        private bool _turingPanelC = false;
        public bool TuringPanelC
        {
            get { return _turingPanelC; }
            set { SetProperty(ref _turingPanelC, value); }
        }

        private string _turingPanelCPort = String.Empty;
        public string TuringPanelCPort
        {
            get { return _turingPanelCPort; }
            set { SetProperty(ref _turingPanelCPort, value); }
        }

        private Guid _turingPanelCProfile = Guid.Empty;
        public Guid TuringPanelCProfile
        {
            get { return _turingPanelCProfile; }
            set { SetProperty(ref _turingPanelCProfile, value); }
        }

        private LCD_ROTATION _turingPanelCRotation = 0;
        public LCD_ROTATION TuringPanelCRotation
        {
            get { return _turingPanelCRotation; }
            set
            {
                SetProperty(ref _turingPanelCRotation, value);
            }
        }

        private bool _webServer = false;
        public bool WebServer
        {
            get { return _webServer; }
            set { SetProperty(ref _webServer, value); }
        }

        private string _webServerListenIp = "127.0.0.1";
        public string WebServerListenIp
        {
            get { return _webServerListenIp; }
            set { SetProperty(ref _webServerListenIp, value); }
        }

        private int _webServerListenPort = 80;
        public int WebServerListenPort
        {
            get { return _webServerListenPort; }
            set { SetProperty(ref _webServerListenPort, value); }
        }

        private int _webServerRefreshRate = 100;
        public int WebServerRefreshRate
        {
            get { return _webServerRefreshRate; }
            set { SetProperty(ref _webServerRefreshRate, value); }
        }

        private int _targetFrameRate = 15;
        public int TargetFrameRate
        {
            get { return _targetFrameRate; }
            set { SetProperty(ref _targetFrameRate, value); }
        }

        private int _targetGraphUpdateRate = 1000;
        public int TargetGraphUpdateRate
        {
            get { return _targetGraphUpdateRate; }
            set { SetProperty(ref _targetGraphUpdateRate, value); }
        }

        private int _version = 114;
        public int Version
        {
            get { return _version; }
            set { SetProperty(ref _version, value); }
        }
    }
}
