using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace InfoPanel.SensorHelper;

/// <summary>
/// LibreHardwareMonitor's computer as the helper's tree, read as 1.4.x read it (<c>LibreMonitor.cs</c>): the groups of
/// <see cref="HelperGroups"/>, storage read every few seconds by its setting, every piece of hardware updated each
/// round, and the sensors of each piece and of its sub-hardware listed with their identifiers as LibreHardwareMonitor
/// writes them (<c>Identifier.ToString()</c>, the <c>libre:</c> keys 1.4.x stored). It reads only: no sensor's
/// <c>Control</c> is ever touched, so fan control is out of reach. A computer whose <c>Open()</c> throws is left as it
/// is (LibreHardwareMonitor's <c>Close()</c> does nothing for one it doesn't count as open, so what it opened part way
/// can't be closed) and no other is ever opened in this process: the helper ends, and that frees it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class LibreTree : ISensorTree
{
    private Computer? _computer;
    private bool _failed;

    public bool Elevated => Environment.IsPrivilegedProcess;

    public string Driver => "unknown";

    public void Configure(bool storage, int storageInterval)
    {
        if (_failed)
            throw new InvalidOperationException("LibreHardwareMonitor's computer failed to open; the helper opens no other.");
        Close();
        var groups = HelperGroups.For(storage);
        StorageDevice.ThrottleInterval = TimeSpan.FromSeconds(storageInterval);
        var computer = new Computer
        {
            IsCpuEnabled = groups.Cpu,
            IsGpuEnabled = groups.Gpu,
            IsMemoryEnabled = groups.Memory,
            IsMotherboardEnabled = groups.Motherboard,
            IsControllerEnabled = groups.Controller,
            IsNetworkEnabled = groups.Network,
            IsStorageEnabled = groups.Storage,
            IsPsuEnabled = groups.Psu,
            IsBatteryEnabled = groups.Battery,
            IsPowerMonitorEnabled = groups.PowerMonitor,
        };
        _failed = true; // until Open() returns
        computer.Open();
        (_computer, _failed) = (computer, false);
    }

    public TreeSnapshot Update()
    {
        var computer = _computer ?? throw new InvalidOperationException("LibreHardwareMonitor's computer isn't open.");
        computer.Accept(UpdateVisitor.Instance);

        var hardware = new List<TreeHardware>();
        var sensors = new List<TreeSensor>();
        foreach (var top in computer.Hardware)
        {
            Add(top, null);
            foreach (var sub in top.SubHardware)
                Add(sub, top);
        }

        return new TreeSnapshot(hardware, sensors);

        void Add(IHardware piece, IHardware? parent)
        {
            var id = piece.Identifier.ToString();
            hardware.Add(new TreeHardware(id, piece.Name, piece.HardwareType.ToString(), (int)piece.HardwareType, parent?.Identifier.ToString()));
            foreach (var sensor in piece.Sensors)
                sensors.Add(new TreeSensor(sensor.Identifier.ToString(), sensor.Name, sensor.SensorType.ToString(), sensor.Index, id, sensor.Value, sensor.Min, sensor.Max));
        }
    }

    public void Dispose() => Close();

    private void Close()
    {
        _computer?.Close();
        _computer = null;
    }

    /// <summary>Updates every piece of hardware and its sub-hardware, as 1.4.x's visitor did.</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public static readonly UpdateVisitor Instance = new();

        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
