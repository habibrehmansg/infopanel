using LibreHardwareMonitor.Hardware;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Timers;
using Computer = LibreHardwareMonitor.Hardware.Computer;
namespace InfoPanel.Monitors
{
    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    internal class LibreMonitor
    {
        private static System.Timers.Timer aTimer = new(1000);
        private static ConcurrentDictionary<string, IHardware> HEADER_DICT = new ();
        public static ConcurrentDictionary<string, ISensor> SENSORHASH = new();
        private static Thread? CoreThread;
        private static Computer? Computer;

        private static volatile bool IsOpen = false;
        private static readonly object _lock = new();

        public static bool Launch()
        {
            if (CoreThread != null)
            {
                return true;
            }

            CoreThread = new Thread(TimedStart);
            CoreThread.Start();

            return true;
        }

        private static void Prepare()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            Computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
            };

            Computer.Open();
            
            stopwatch.Stop();
            Trace.WriteLine($"Computer open: {stopwatch.ElapsedMilliseconds}ms");

            aTimer.Start();
        }

        public static void TimedStart()
        {
            Prepare();
            aTimer.Elapsed += new ElapsedEventHandler(PollSensorData);
            aTimer.Enabled = true;
        }

        private static volatile bool polling = false;

        private static void PollSensorData(object? source, ElapsedEventArgs e)
        {
            if(polling)
            {
                return;
            }

            polling = true;
            Monitor();
            polling = false;
        }

        private static readonly UpdateVisitor _updateVisitor = new();

        public static void Monitor()
        {
            if(Computer == null)
            {
                return;
            }

            Computer.Accept(_updateVisitor);
           
            foreach (IHardware hardware in Computer.Hardware)
            {
                //Trace.WriteLine($"Hardware: {hardware.Name} id: {hardware.Identifier}");
                HEADER_DICT[hardware.Identifier.ToString()] = hardware;

                foreach (IHardware subhardware in hardware.SubHardware)
                {
                    //Trace.WriteLine($"\tSubhardware: {subhardware.Name} id: {subhardware.Identifier}");

                    foreach (ISensor sensor in subhardware.Sensors)
                    {
                        //Trace.WriteLine($"\t\tSensor: {sensor.Name}, value: {sensor.Value} id: {sensor.Identifier}");
                        SENSORHASH[sensor.Identifier.ToString()] = sensor;
                    }
                }

                foreach (ISensor sensor in hardware.Sensors)
                {
                    //Trace.WriteLine($"\tSensor: {sensor.Name}, value: {sensor.Value} id: {sensor.Identifier}");
                    SENSORHASH[sensor.Identifier.ToString()] = sensor;
                }
            }
        }

        public static List<ISensor> GetOrderedList()
        {
            List<ISensor> OrderedList = [.. SENSORHASH.Values.OrderBy(x => x.Hardware.HardwareType).ThenBy(x => x.Index)];
            return OrderedList;
        }
    }

    public static class ISensorExtensions
    {
        private static readonly Dictionary<SensorType, string> Units = new()
        {
            { SensorType.Voltage, "V" },
            { SensorType.Current, "A" },
            { SensorType.Clock, "MHz" },
            { SensorType.Temperature, "°C" },
            { SensorType.Load, "%" },
            { SensorType.Fan, "RPM" },
            { SensorType.Flow, "L/h" },
            { SensorType.Control, "%" },
            { SensorType.Level, "%" },
            { SensorType.Factor, "1" },
            { SensorType.Power, "W" },
            { SensorType.Data, "GB" },
            { SensorType.Frequency, "Hz" },
            { SensorType.Energy, "mWh" },
            { SensorType.Noise, "dBA" },
            { SensorType.Conductivity, "µS/cm" },
            { SensorType.Humidity, "%" },
            { SensorType.Throughput, "KB/s" },
        };

        public static string GetUnit(this ISensor sensor)
        {
            return Units.GetValueOrDefault(sensor.SensorType, "");
        }
    }
}
