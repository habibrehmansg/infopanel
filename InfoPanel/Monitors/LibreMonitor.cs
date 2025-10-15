﻿using LibreHardwareMonitor.Hardware;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    internal class LibreMonitor : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<LibreMonitor>();
        private static readonly Lazy<LibreMonitor> _instance = new(() => new LibreMonitor());
        public static LibreMonitor Instance => _instance.Value;

        private static readonly ConcurrentDictionary<string, IHardware> HEADER_DICT = new();
        public static readonly ConcurrentDictionary<string, ISensor> SENSORHASH = new();

        private LibreMonitor() { }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                Computer computer = new()
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true,
                    IsNetworkEnabled = true,
                    IsStorageEnabled = true,
                };

                computer.Open();

                stopwatch.Stop();
                Logger.Information("LibreHardwareMonitor computer opened in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

                try
                {
                    UpdateVisitor updateVisitor = new();

                    while (!token.IsCancellationRequested)
                    {
                        computer.Accept(updateVisitor);

                        foreach (IHardware hardware in computer.Hardware)
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

                        await Task.Delay(1000, token);

                    }
                }
                catch (TaskCanceledException)
                {
                    Logger.Debug("LibreMonitor task cancelled");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Exception during LibreMonitor work");
                }
                finally
                {
                    computer.Close();
                    HEADER_DICT.Clear();
                    SENSORHASH.Clear();
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "LibreMonitor initialization error");
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
