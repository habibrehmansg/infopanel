using InfoPanel.Plugins;
using Microsoft.Diagnostics.Tracing; // Ensure you have the correct NuGet package
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Reflection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace InfoPanel.Extras
{
    public class FPSPlugin : BasePlugin
    {
        private TraceEventSession? _etwSession;
        private readonly long _startTimestamp = Stopwatch.GetTimestamp();
        private readonly ConcurrentDictionary<int, TimestampCollection> _frames = new();
        private readonly PluginSensor _fps = new("fps", "Frames Per Second", 0, "FPS");

        public FPSPlugin() : base("fps-plugin", "FPS Info - DirectX", "Displays the current FPS using DirectX.")
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
            _etwSession.EnableProvider("Microsoft-Windows-D3D9");
            _etwSession.EnableProvider("Microsoft-Windows-DXGI");

            _etwSession.Source.AllEvents += data =>
            {
                if ((int)data.ID == 42 && data.ProviderGuid == Guid.Parse("{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}"))
                {
                    int pid = data.ProcessID;
                    long timestamp = Stopwatch.GetTimestamp();

                    var frame = _frames.GetOrAdd(pid, _ =>
                    {
                        var collection = new TimestampCollection
                        {
                            Name = Process.GetProcessById(pid)?.ProcessName ?? pid.ToString()
                        };
                        return collection;
                    });

                    frame.Add(timestamp);
                }
            };

            Thread etwThread = new(EtwThreadProc) { IsBackground = true };
            etwThread.Start();
        }

        private void EtwThreadProc()
        {
            try
            {
                _etwSession?.Source.Process();
            }
            catch (Exception ex)
            {
                // Log or handle the exception as needed
                Console.WriteLine($"ETW session error: {ex.Message}");
            }
            finally
            {
                _etwSession?.Dispose();
            }
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
            long t1, t2;
            long dt = Stopwatch.Frequency;

            t2 = Stopwatch.GetTimestamp();
            t1 = t2 - dt;

            foreach (var frame in _frames.Values)
            {
                int count = frame.QueryCount(t1, t2);
                _fps.Value = (float)count / dt * Stopwatch.Frequency;
            }
        }
    }

    public class TimestampCollection
    {
        private const int MAXNUM = 1000;
        public string Name { get; set; } = string.Empty;
        private readonly ConcurrentBag<long> _timestamps = new();

        public void Add(long timestamp)
        {
            _timestamps.Add(timestamp);
            if (_timestamps.Count > MAXNUM)
            {
                _timestamps.TryTake(out _);
            }
        }

        public int QueryCount(long from, long to)
        {
            return _timestamps.Count(ts => ts >= from && ts <= to);
        }

        public double GetFrameTime(int count)
        {
            double returnValue = 0;

            var timestamps = _timestamps.ToArray();
            int listCount = timestamps.Length;

            if (listCount > count)
            {
                for (int i = 1; i <= count; i++)
                {
                    returnValue += timestamps[listCount - i] - timestamps[listCount - (i + 1)];
                }

                returnValue /= count;
            }

            return returnValue;
        }
    }
}