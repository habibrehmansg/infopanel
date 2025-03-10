using InfoPanel.Plugins;
using System;
using System.Data;
using System.Diagnostics;

namespace InfoPanel.Extras
{
    public class SystemInfoPlugin : BasePlugin
    {
        private readonly PluginText _uptimeFormattedSensor = new("Formatted", "-");
        private readonly PluginText _uptimeDaysSensor = new("Days", "-");
        private readonly PluginText _uptimeHoursSensor = new("Hours", "-");
        private readonly PluginText _uptimeMinutesSensor = new("Minutes", "-");
        private readonly PluginText _uptimeSecondsSensor = new("Seconds", "-");

        private readonly PluginSensor _processCountSensor = new("Process Count", 0);
        private readonly PluginSensor _threadCountSensor = new("Thread Count", 0);
        private readonly PluginSensor _handleCountSensor = new("Handle Count", 0);

        private readonly PluginSensor _cpuUsage = new("CPU Usage", 0, "%");
        private readonly PluginSensor _cpuUtility = new("CPU Utility", 0, "%");
        private readonly PluginSensor _memoryUsage = new("Memory Usage", 0, " MB");
        private readonly PluginSensor _memoryCompression = new("Memory Compression", 0, " MB");

        private static readonly string _defaultTopFormat = "0:200|1:60|2:70|3:100";
        private readonly PluginTable _topCpuUsage = new("Top CPU Usage", new DataTable(), _defaultTopFormat);
        private readonly PluginTable _topCpuUtility = new("Top CPU Utility", new DataTable(), _defaultTopFormat);
        private readonly PluginTable _topMemoryUsage = new("Top Memory Usage", new DataTable(), _defaultTopFormat);

        public override string? ConfigFilePath => Config.FilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        private string[] blacklist = [];

        public SystemInfoPlugin() : base("system-info-plugin", "System Info", "Misc system information and statistics.")
        {
        }

        public override void Initialize()
        {
            Config.Instance.Load();
            if(Config.Instance.TryGetValue(Config.SECTION_SYSTEM_INFO, "Blacklist", out var result))
            {
                blacklist = result.Split(',');
            }
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Uptime");
            container.Entries.Add(_uptimeFormattedSensor);
            container.Entries.Add(_uptimeDaysSensor);
            container.Entries.Add(_uptimeHoursSensor);
            container.Entries.Add(_uptimeMinutesSensor);
            container.Entries.Add(_uptimeSecondsSensor);

            containers.Add(container);

            container = new PluginContainer("Processes");
            container.Entries.Add(_processCountSensor);
            container.Entries.Add(_threadCountSensor);
            container.Entries.Add(_handleCountSensor);
            container.Entries.Add(_cpuUsage);
            container.Entries.Add(_cpuUtility);
            container.Entries.Add(_memoryUsage);
            container.Entries.Add(_memoryCompression);
            container.Entries.Add(_topCpuUsage);
            container.Entries.Add(_topCpuUtility);
            container.Entries.Add(_topMemoryUsage);

            containers.Add(container);
        }

        public override void Close()
        {
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            GetUptime();
            GetProcessInfo();

            return Task.CompletedTask;
        }

        private void GetUptime()
        {
            // Get the system uptime in milliseconds
            long uptimeMilliseconds = Environment.TickCount64;

            // Convert milliseconds to TimeSpan
            TimeSpan uptime = TimeSpan.FromMilliseconds(uptimeMilliseconds);

            _uptimeFormattedSensor.Value = $"{uptime.Days}:{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
            _uptimeDaysSensor.Value = $"{uptime.Days:D2}";
            _uptimeHoursSensor.Value = $"{uptime.Hours:D2}";
            _uptimeMinutesSensor.Value = $"{uptime.Minutes:D2}";
            _uptimeSecondsSensor.Value = $"{uptime.Seconds:D2}";
        }

       
        PerformanceCounter? utility;
        PerformanceCounter? usage;

        Dictionary<string, double> firstUsageSample = [];

        private void GetProcessInfo()
        {
            var processes = Process.GetProcesses();
            _processCountSensor.Value = processes.Length;

            int totalThreadCount = 0;
            int totalHandleCount = 0;

            foreach (var process in processes)
            {
                try
                {
                    // Add the number of threads for the process to the total
                    totalThreadCount += process.Threads.Count;

                    // Add the handle count for the process to the total
                    totalHandleCount += process.HandleCount;
                }
                catch (Exception ex)
                {
                    // Handle any exceptions that might occur (e.g., access denied)
                    Console.WriteLine($"Could not access process {process.ProcessName}: {ex.Message}");
                }
            }

            _threadCountSensor.Value = totalThreadCount;
            _handleCountSensor.Value = totalHandleCount;

            var processGroups = processes.GroupBy(p => p.ProcessName);

            utility ??= new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            usage ??= new PerformanceCounter("Processor Information", "% Processor Time", "_Total");

            var utilityDelta = utility.NextValue();
            _cpuUtility.Value = utilityDelta;

            var usageDelta = usage.NextValue();
            _cpuUsage.Value = usageDelta;

            var category = new PerformanceCounterCategory("Process V2");
            var categoryData = category.ReadCategory();
            var usageData = categoryData["% Processor Time"];
            var memoryData = categoryData["Working Set - Private"];

            // Second sample
            var secondUsageSample = new Dictionary<string, double>();
            foreach (InstanceData data in usageData.Values)
            {
                secondUsageSample[data.InstanceName] = data.RawValue;
            }

            var memorySample = new Dictionary<string, long>();
            foreach (InstanceData data in memoryData.Values)
            {
                memorySample[data.InstanceName] = data.RawValue;
            }

            Dictionary<string, Instance> instances = [];

            if (firstUsageSample.TryGetValue("_Total", out var firstTotalValue) && secondUsageSample.TryGetValue("_Total", out var secondTotalValue))
            {
                var totalDelta = secondTotalValue - firstTotalValue;

                foreach (var kvp in secondUsageSample)
                {
                    var proceessName = kvp.Key.Split(':', 2)[0];

                    if (firstUsageSample.TryGetValue(kvp.Key, out var firstValue) && memorySample.TryGetValue(kvp.Key, out var memoryValue))
                    {
                        double secondValue = kvp.Value;
                        double delta = secondValue - firstValue;

                        double cpuUsage = (delta / totalDelta) * 100;
                        double cpuUtility = 0.0;
                        if (usageDelta > 0 && cpuUsage <= usageDelta)
                        {
                            cpuUtility = cpuUsage / usageDelta * utilityDelta;
                        }

                        if (instances.TryGetValue(proceessName, out var instance))
                        {
                            instance.Usage += cpuUsage;
                            instance.Utility += cpuUtility;
                            instance.PrivateMemory += memoryValue;
                        }
                        else
                        {
                            instances.TryAdd(proceessName, new Instance { Name = proceessName, Usage = cpuUsage, Utility = cpuUtility, PrivateMemory = memoryValue });
                        }
                    }
                }
            }

            if(instances.TryGetValue("_Total", out var totalInstance))
            {
                _memoryUsage.Value = (float) totalInstance.PrivateMemory / 1024 / 1024;
            }

            if (instances.TryGetValue("Memory Compression", out var memoryCompression))
            {
                _memoryCompression.Value = (float) memoryCompression.PrivateMemory / 1024 / 1024;
            }


            var result = instances.Values.ToList();

            //cpu usage
            result.Sort((a, b) => b.Usage.CompareTo(a.Usage));
            _topCpuUsage.Value = BuildDataTable(result, blacklist);

            //cpu utility
            result.Sort((a, b) => b.Utility.CompareTo(a.Utility));
            _topCpuUtility.Value = BuildDataTable(result, blacklist);

            //memory
            result.Sort((a, b) => b.PrivateMemory.CompareTo(a.PrivateMemory));
            _topMemoryUsage.Value = BuildDataTable(result, blacklist);

            //set default
            firstUsageSample = secondUsageSample;
        }

        private static DataTable BuildDataTable(List<Instance> instances, string[] blacklist)
        {
            var dataTable = new DataTable();

            dataTable.Columns.Add("Process Name", typeof(PluginText));
            dataTable.Columns.Add("Usage", typeof(PluginSensor));
            dataTable.Columns.Add("Utility", typeof(PluginSensor));
            dataTable.Columns.Add("Memory", typeof(PluginSensor));

            foreach(var instance in instances)
            {
                if (blacklist.Contains(instance.Name))
                {
                    continue;
                }

                var row = dataTable.NewRow();
                row[0] = new PluginText("Process Name", instance.Name);
                row[1] = new PluginSensor("Usage", (float)instance.Usage, "%");
                row[2] = new PluginSensor("Utility", (float)instance.Utility, "%");
                row[3] = new PluginSensor("Memory", (float) (instance.PrivateMemory) / 1024 / 1024, " MB");
                dataTable.Rows.Add(row);
            }

            return dataTable;
        }

        class Instance
        {
            public required string Name { get; set; }
            public double Usage { get; set; }
            public double Utility { get; set; }
            public long PrivateMemory { get; set; }
        }
    }
}
