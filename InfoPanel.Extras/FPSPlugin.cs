using InfoPanel.Plugins;
using Microsoft.Diagnostics.Tracing; // Ensure NuGet package is installed to InfoPanel.Plugins project
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace InfoPanel.Extras
{
    public class FPSPlugin : BasePlugin
    {
        private TraceEventSession? _etwSession;
        private readonly PluginSensor _fps = new("fps", "Frames Per Second", 0, "FPS");

        private static readonly Guid DXGI_provider = Guid.Parse("{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}");
        private static readonly Guid D3D9_provider = Guid.Parse("{783ACA0A-790E-4D7F-8451-AA850511C6B9}");
        private const int EventID_D3D9PresentStart = 1;
        private const int EventID_DxgiPresentStart = 42;

        private DateTime _lastTime = DateTime.Now;
        private int _framesRendered = 0;
        private int _fpsValue = 0;

        public FPSPlugin() : base("fps-plugin", "FPS Info - Simple", "Displays the current FPS using DirectX.")
        {
        }

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            _etwSession = new TraceEventSession("fps-session")
            {
                StopOnDispose = true
            };
            _etwSession.EnableProvider(D3D9_provider);
            _etwSession.EnableProvider(DXGI_provider);

            _etwSession.Source.AllEvents += data =>
            {
                if ((int)data.ID == EventID_D3D9PresentStart && data.ProviderGuid == D3D9_provider ||
                    (int)data.ID == EventID_DxgiPresentStart && data.ProviderGuid == DXGI_provider)
                {
                    _framesRendered++;
                }
            };

            Thread etwThread = new(EtwThreadProc) { IsBackground = true };
            etwThread.Start();
        }

        private void EtwThreadProc()
        {
            _etwSession?.Source.Process();
        }

        public override void Close()
        {
            _etwSession?.Dispose();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fps);
            containers.Add(container);
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            await Task.Run(() => CalculateFPS());
        }

        private void CalculateFPS()
        {
            if ((DateTime.Now - _lastTime).TotalSeconds >= 1)
            {
                // One second has elapsed
                _fpsValue = _framesRendered;
                _framesRendered = 0;
                _lastTime = DateTime.Now;
            }

            _fps.Value = _fpsValue;
        }
    }

    public class TimestampCollection
    {
        private const int MAXNUM = 1000;
        public string Name { get; set; } = string.Empty;
        private readonly List<long> _timestamps = new(MAXNUM + 1);
        private readonly object _sync = new();

        public void Add(long timestamp)
        {
            lock (_sync)
            {
                _timestamps.Add(timestamp);
                if (_timestamps.Count > MAXNUM) _timestamps.RemoveAt(0);
            }
        }

        public int QueryCount(long from, long to)
        {
            lock (_sync)
            {
                return _timestamps.Count(ts => ts >= from && ts <= to);
            }
        }

        public double GetFrameTime(int count)
        {
            double returnValue = 0;
            int listCount = _timestamps.Count;

            if (listCount > count)
            {
                for (int i = 1; i <= count; i++)
                {
                    returnValue += _timestamps[listCount - i] - _timestamps[listCount - (i + 1)];
                }

                returnValue /= count;
            }

            return returnValue;
        }
    }
}
