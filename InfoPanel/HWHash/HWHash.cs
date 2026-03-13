using InfoPanel;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Timers;

    public class HWHash
    {
        private static readonly ILogger Logger = Log.ForContext<HWHash>();

        //Initially it was based on zipferot3000 implementation, but he was using NewtonsoftJSON which I avoid, so I modified it to use MsgPack
        //Since most users are not familiar with MsgPack I am using the default serializer without external dependencies and some minor tweaks.


        const string SHARED_MEM_PATH = "Global\\HWiNFO_SENS_SM2";
        const string REMOTE_SHARED_MEM_PREFIX = "Global\\HWiNFO_SENS_SM2_REMOTE_";
        const int SENSOR_STRING_LEN = 128;
        const int READING_STRING_LEN = 16;

        private static readonly Stopwatch SW = Stopwatch.StartNew();

        private static HWHashStats SelfData;

        private static int IndexOrder = 0;

        private static System.Timers.Timer aTimer = new(1000);

        private static readonly List<ShmConnection> _connections = new();
        private static int _reprobeCounter = 0;
        private const int REPROBE_INTERVAL = 5; // re-probe every 5 poll cycles

        public static ConcurrentDictionary<(int, UInt32, UInt32, UInt32), HWINFO_HASH> SENSORHASH = new ConcurrentDictionary<(int, UInt32, UInt32, UInt32), HWINFO_HASH>();
        public static ConcurrentDictionary<(int, UInt32, UInt32, UInt32), HWINFO_HASH_MINI> SENSORHASH_MINI = new ConcurrentDictionary<(int, UInt32, UInt32, UInt32), HWINFO_HASH_MINI>();
        private static Thread? CoreThread;

        /// <summary>
        /// If [true], the thread will run at a high priority to avoid being delayed by other tasks.
        /// </summary>
        public static bool HighPriority = false;
        /// <summary>
        /// If [true], it will enable 1ms resolution, much better for newer systems, beware that it is a WIN32API call and all process are affected.
        /// </summary>
        public static bool HighPrecision = false;
        /// <summary>
        /// If [true] the measurements will be taken at precise intervals (every 1000ms) for instance.
        /// </summary>
        private static bool RoundMS = false; //edit: Not needed now [?]
        /// <summary>
        /// Delay in milliseconds (ms) between each update. Default is 1000 [ms], minimum is 100 [ms], maximum is 60000 [ms]. Make sure you configure HWInfo interval too so it will pull at the same rate.
        /// </summary>
        /// <param name="delayms">Time in milliseconds to wait, minimum 100, max 60000.</param>
        /// <returns>true if new delay is between the safe range, false otherwise.</returns>
        public static bool SetDelay(int delayms)
        {
            if (delayms < 20 || delayms > 60000)
            {
                return false;
            }
            aTimer.Interval = delayms;
            return true;
        }

        public static bool IsRunning()
        {
            return CoreThread != null;
        }


        public static bool Launch()
        {
            if (CoreThread != null)
            {
                return true;
            }

            CoreThread = new Thread(TimedStart);

            if (HighPriority == true) { CoreThread.Priority = ThreadPriority.Highest; }

            if (HighPrecision == true) { _ = WinApi.TimeBeginPeriod(1); }

            CoreThread.Start();

            return true;
        }

        private static bool HWINFO_RUNNING = false;
        private static void Prepare()
        {
            // Try to open local SHM
            var localConn = new ShmConnection
            {
                RemoteIndex = -1,
                ShmPath = SHARED_MEM_PATH
            };

            if (ReadMem(localConn))
            {
                lock (_connections)
                {
                    if (!_connections.Exists(c => c.RemoteIndex == -1))
                    {
                        _connections.Add(localConn);
                    }
                }

                BuildHeaders(localConn);
                HWINFO_RUNNING = true;
                SharedModel.Instance.HwInfoAvailable = true;

                // Probe for remote connections
                ProbeRemoteConnections();
            }
            else
            {
                SharedModel.Instance.HwInfoAvailable = false;
                aTimer.Stop();
                Thread.Sleep(1000);
                aTimer.Start();
            }
        }

        private static void ProbeRemoteConnections()
        {
            for (int i = 1; ; i++)
            {
                bool alreadyExists;
                lock (_connections)
                {
                    alreadyExists = _connections.Exists(c => c.RemoteIndex == i);
                }

                if (alreadyExists)
                    continue;

                var remoteConn = new ShmConnection
                {
                    RemoteIndex = i,
                    ShmPath = REMOTE_SHARED_MEM_PREFIX + i
                };

                if (ReadMem(remoteConn))
                {
                    BuildHeaders(remoteConn);
                    lock (_connections)
                    {
                        _connections.Add(remoteConn);
                    }
                    Logger.Information("Discovered remote HWiNFO connection {Index}: {Path}", i, remoteConn.ShmPath);
                }
                else
                {
                    break; // Stop probing when we hit the first missing remote
                }
            }
        }

        public static void TimedStart()
        {
            aTimer.Elapsed += new ElapsedEventHandler(PollSensorData);
            aTimer.Enabled = true;
        }

        private static void PollSensorData(object source, ElapsedEventArgs e)
        {
            if (HWINFO_RUNNING)
            {
                // Re-probe for new remote connections periodically
                _reprobeCounter++;
                if (_reprobeCounter >= REPROBE_INTERVAL)
                {
                    _reprobeCounter = 0;
                    ProbeRemoteConnections();
                }

                List<ShmConnection> snapshot;
                lock (_connections)
                {
                    snapshot = new List<ShmConnection>(_connections);
                }

                foreach (var conn in snapshot)
                {
                    if (CheckMem(conn))
                    {
                        ReadSensors(conn);
                    }
                    else
                    {
                        Logger.Warning("HWiNFO shared memory is dead for {Connection}", conn.RemoteIndex == -1 ? "Local" : $"Remote {conn.RemoteIndex}");

                        // Remove keys for this connection
                        var keysToRemove = SENSORHASH.Keys.Where(k => k.Item1 == conn.RemoteIndex).ToList();
                        foreach (var key in keysToRemove)
                        {
                            SENSORHASH.TryRemove(key, out _);
                            SENSORHASH_MINI.TryRemove(key, out _);
                        }

                        conn.MemAcc?.Dispose();
                        conn.MemAcc = null;
                        conn.MemMap?.Dispose();
                        conn.MemMap = null;
                        conn.HeaderDict.Clear();
                        conn.IsAlive = false;

                        lock (_connections)
                        {
                            _connections.Remove(conn);
                        }

                        // If local connection died, mark as not running
                        if (conn.RemoteIndex == -1)
                        {
                            HWINFO_RUNNING = false;
                            SENSORHASH.Clear();
                            SENSORHASH_MINI.Clear();
                            lock (_connections)
                            {
                                // Clean up all remaining connections too
                                foreach (var c in _connections)
                                {
                                    c.MemAcc?.Dispose();
                                    c.MemMap?.Dispose();
                                    c.HeaderDict.Clear();
                                }
                                _connections.Clear();
                            }
                        }
                    }
                }
            }
            else
            {
                Prepare();
            }
        }

        private static bool ReadMem(ShmConnection conn)
        {
            Logger.Debug("Reading HWiNFO shared memory: {Path}", conn.ShmPath);
            conn.MemRegion = new HWINFO_MEM();
            try
            {
                conn.MemMap = MemoryMappedFile.OpenExisting(conn.ShmPath, MemoryMappedFileRights.Read);
                conn.MemAcc = conn.MemMap.CreateViewAccessor(0L, Marshal.SizeOf(typeof(HWINFO_MEM)), MemoryMappedFileAccess.Read);
                conn.MemAcc.Read(0L, out HWINFO_MEM region);
                conn.MemRegion = region;
                conn.IsAlive = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool CheckMem(ShmConnection conn)
        {
            bool result = false;
            try
            {
                if (conn.MemAcc != null)
                {
                    conn.MemAcc.Read(0L, out HWINFO_MEM region);
                    conn.MemRegion = region;
                    byte[] bytes = BitConverter.GetBytes(conn.MemRegion.Sig);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(bytes);
                    }
                    string stringValue = Encoding.UTF8.GetString(bytes);

                    result = stringValue != "DEAD";
                }
            }
            catch (Exception) { }

            return result;
        }

        private static void BuildHeaders(ShmConnection conn)
        {
            Logger.Debug("Building HWiNFO sensor headers for {Connection}", conn.RemoteIndex == -1 ? "Local" : $"Remote {conn.RemoteIndex}");
            if (conn.MemMap != null)
            {
                for (uint index = 0; index < conn.MemRegion.SS_SensorElements; ++index)
                {
                    using (MemoryMappedViewStream viewStream = conn.MemMap.CreateViewStream(conn.MemRegion.SS_OFFSET + index * conn.MemRegion.SS_SIZE, conn.MemRegion.SS_SIZE, MemoryMappedFileAccess.Read))
                    {
                        byte[] buffer = new byte[(int)conn.MemRegion.SS_SIZE];
                        viewStream.Read(buffer, 0, (int)conn.MemRegion.SS_SIZE);
                        GCHandle gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                        HWHASH_HEADER structure = (HWHASH_HEADER)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(HWHASH_HEADER));
                        if (!conn.HeaderDict.ContainsKey(index))
                        {
                            conn.HeaderDict.Add(index, structure);
                        }
                    }
                }

                if (conn.RemoteIndex == -1)
                {
                    SelfData.TotalCategories = conn.MemRegion.SS_SensorElements;
                }
            }
        }

        private static void ReadSensors(ShmConnection conn)
        {
            if (conn.RemoteIndex == -1)
            {
                MiniBenchmark(0);
                SelfData.TotalEntries = conn.MemRegion.TOTAL_ReadingElements;
            }

            for (uint index = 0; index < conn.MemRegion.TOTAL_ReadingElements; ++index)
            {
                using (MemoryMappedViewStream viewStream = conn.MemMap.CreateViewStream(conn.MemRegion.OFFSET_Reading + index * conn.MemRegion.SIZE_Reading, conn.MemRegion.SIZE_Reading, MemoryMappedFileAccess.Read))
                {
                    byte[] buffer = new byte[(int)conn.MemRegion.SIZE_Reading];
                    viewStream.Read(buffer, 0, (int)conn.MemRegion.SIZE_Reading);
                    GCHandle gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    HWHASH_ELEMENT structure = (HWHASH_ELEMENT)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(HWHASH_ELEMENT));
                    FormatSensor(conn, structure);
                    gcHandle.Free();
                }
            }

            if (conn.RemoteIndex == -1)
            {
                MiniBenchmark(1);
            }
        }

        private static void FormatSensor(ShmConnection conn, HWHASH_ELEMENT READING)
        {
            var KEY = (conn.RemoteIndex, conn.HeaderDict[READING.Index].ID, conn.HeaderDict[READING.Index].Instance, READING.ID);
            ulong UNIQUE_ID = FastConcat(READING.ID, READING.Index);
            bool FirstTest = SENSORHASH.ContainsKey(KEY);
            if (FirstTest == false)
            {
                HWINFO_HASH_MINI LastReading_Mini = new()
                {
                    UniqueID = UNIQUE_ID,
                    NameCustom = READING.NameCustom,
                    Unit = READING.Unit,
                    ValueNow = READING.Value,
                    IndexOrder = IndexOrder,
                    ReadingType = TypeToString(READING.SENSOR_TYPE),
                    ValuePrev = READING.Value
                };

                HWINFO_HASH LastReading = new()
                {
                    ReadingType = TypeToString(READING.SENSOR_TYPE),
                    SensorIndex = READING.Index,
                    SensorID = READING.ID,
                    UniqueID = UNIQUE_ID,
                    NameDefault = READING.NameDefault,
                    NameCustom = READING.NameCustom,
                    Unit = READING.Unit,
                    ValuePrev = READING.Value,
                    ValueNow = READING.Value,
                    ValueMin = READING.ValueMin,
                    ValueMax = READING.ValueMax,
                    ValueAvg = READING.ValueAvg,
                    ParentNameDefault = conn.HeaderDict[READING.Index].NameDefault,
                    ParentNameCustom = conn.HeaderDict[READING.Index].NameCustom,
                    ParentID = conn.HeaderDict[READING.Index].ID,
                    ParentInstance = conn.HeaderDict[READING.Index].Instance,
                    ParentUniqueID = FastConcat(conn.HeaderDict[READING.Index].ID, conn.HeaderDict[READING.Index].Instance),
                    IndexOrder = IndexOrder++,
                    RemoteIndex = conn.RemoteIndex

                };
                SENSORHASH.TryAdd(KEY, LastReading);
                SENSORHASH_MINI.TryAdd(KEY, LastReading_Mini);
            }
            else
            {
                HWINFO_HASH T = SENSORHASH[KEY];
                T.ValuePrev = T.ValueNow;
                T.ValueNow = READING.Value;
                T.ValueMin = READING.ValueMin;
                T.ValueMax = READING.ValueMax;
                T.ValueAvg = READING.ValueAvg;

                SENSORHASH[KEY] = T;

                HWINFO_HASH_MINI TMINI = SENSORHASH_MINI[KEY];
                TMINI.ValuePrev = TMINI.ValueNow;
                TMINI.ValueNow = READING.Value;

                SENSORHASH_MINI[KEY] = TMINI;
            }
        }

        // This is quite dumb but I don't want to rewrite this code now, can definitely be improved, should ran against the Enum @ Reading time.
        private static string TypeToString(SENSOR_READING_TYPE IN)
        {
            string OUT = "Unknown";
            switch (IN)
            {
                case SENSOR_READING_TYPE.SENSOR_TYPE_NONE:
                    OUT = "None";
                    break;
                case SENSOR_READING_TYPE.SENSOR_TYPE_TEMP:
                    OUT = "Temperature";
                    break;
                case SENSOR_READING_TYPE.SENSOR_TYPE_VOLT:
                    OUT = "Voltage";
                    break;
                case SENSOR_READING_TYPE.SENSOR_TYPE_FAN:
                    OUT = "Fan";
                    break;
                case SENSOR_READING_TYPE.SENSOR_TYPE_CURRENT:
                    OUT = "Current";
                    break;
                case SENSOR_READING_TYPE.SENSOR_TYPE_POWER:
                    OUT = "Power";
                    break;
                case SENSOR_READING_TYPE.SENSOR_TYPE_CLOCK:
                    OUT = "Frequency";
                    break;
                case SENSOR_READING_TYPE.SENSOR_TYPE_USAGE:
                    OUT = "Usage";
                    break;
                case SENSOR_READING_TYPE.SENSOR_TYPE_OTHER:
                    OUT = "Other";
                    break;
            }

            return OUT;
        }
        /// <summary>
        /// Get basic information about collection time (in milliseconds), total entries, etc...
        /// </summary>
        /// <returns>Returns a struct [HWHashStats] containing information about HWHash running thread.</returns>
        public static HWHashStats GetHWHashStats()
        {
            return SelfData;
        }

        private static void MiniBenchmark(int Mode)
        {
            if (Mode == 0)
            {
                SW.Restart();
            }

            if (Mode == 1)
            {
                SelfData.CollectionTime = SW.ElapsedMilliseconds;
            }
        }

        /// <summary>
        /// Returns a list respecting the same order as HWInfo original user interface.
        /// </summary>
        public static List<HWINFO_HASH> GetOrderedList()
        {
            List<HWINFO_HASH> OrderedList = SENSORHASH.Values.OrderBy(x => x.IndexOrder).ToList();
            return OrderedList;
        }

        /// <summary>
        /// Returns a list filtered by remote index, respecting the same order as HWInfo original user interface.
        /// </summary>
        public static List<HWINFO_HASH> GetOrderedList(int remoteIndex)
        {
            List<HWINFO_HASH> OrderedList = SENSORHASH.Values
                .Where(x => x.RemoteIndex == remoteIndex)
                .OrderBy(x => x.IndexOrder)
                .ToList();
            return OrderedList;
        }

        /// <summary>
        /// Returns a list respecting the same order as HWInfo original user interface, in a minified version.
        /// </summary>
        public static List<HWINFO_HASH_MINI> GetOrderedListMini()
        {
            List<HWINFO_HASH_MINI> OrderedList = SENSORHASH_MINI.Values.OrderBy(x => x.IndexOrder).ToList();
            return OrderedList;
        }

        /// <summary>
        /// Returns a list of available connections (local + remotes) with display names.
        /// </summary>
        public static List<(int remoteIndex, string name)> GetAvailableConnections()
        {
            var result = new List<(int, string)>();
            List<ShmConnection> snapshot;
            lock (_connections)
            {
                snapshot = new List<ShmConnection>(_connections);
            }

            foreach (var conn in snapshot.OrderBy(c => c.RemoteIndex))
            {
                if (conn.RemoteIndex == -1)
                {
                    result.Add((-1, "Local"));
                }
                else
                {
                    string name = $"Remote {conn.RemoteIndex}";
                    if (conn.HeaderDict.Count > 0 && conn.HeaderDict.TryGetValue(0, out var header))
                    {
                        var displayName = header.NameDefault;
                        // Extract [Desktop-ID] portion if present (format: "[Desktop-ID] System: ...")
                        if (displayName.StartsWith('['))
                        {
                            var endBracket = displayName.IndexOf(']');
                            if (endBracket > 1)
                            {
                                displayName = displayName.Substring(1, endBracket - 1);
                            }
                        }
                        name = $"Remote {conn.RemoteIndex}: {displayName}";
                    }
                    result.Add((conn.RemoteIndex, name));
                }
            }

            return result;
        }

        /// <summary>
        /// Converts the Dictionary to a JSON string
        /// </summary>
        /// <param name="Order">If set to true, will return in the same order it is displayed on HWInfo UI</param>
        /// <returns></returns>
        public static string GetJsonString(bool Order = false)
        {
            if (Order == true)
            {
                return JsonSerializer.Serialize(SENSORHASH.Values.OrderBy(x => x.IndexOrder).ToList());
            }
            else
            {
                return JsonSerializer.Serialize(SENSORHASH);
            }
        }

        /// <summary>
        /// Converts the Dictionary to a JSON string (Mini version)
        /// </summary>
        /// <param name="Order">If set to true, will return in the same order it is displayed on HWInfo UI</param>
        /// <returns></returns>
        public static string GetJsonStringMini(bool Order = false)
        {
            if (Order == true)
            {
                return JsonSerializer.Serialize(SENSORHASH_MINI.Values.OrderBy(x => x.IndexOrder).ToList());
            }
            else
            {
                return JsonSerializer.Serialize(SENSORHASH_MINI);
            }
        }
        public struct HWHashStats
        {
            public long CollectionTime { get; set; }
            public uint TotalCategories { get; set; }
            public uint TotalEntries { get; set; }
        }

        public record struct HWINFO_HASH
        {
            public string ReadingType { get; set; }
            public uint SensorIndex { get; set; }
            public uint SensorID { get; set; }
            public ulong UniqueID { get; set; }
            public string NameDefault { get; set; }
            public string NameCustom { get; set; }
            public string Unit { get; set; }
            public double ValueNow { get; set; }
            public double ValueMin { get; set; }
            public double ValueMax { get; set; }
            public double ValueAvg { get; set; }
            public double ValuePrev { get; set; }
            public string ParentNameDefault { get; set; }
            public string ParentNameCustom { get; set; }
            public uint ParentID { get; set; }
            public uint ParentInstance { get; set; }
            public ulong ParentUniqueID { get; set; }
            public int IndexOrder { get; set; }
            public int RemoteIndex { get; set; }
        }

        public record struct HWINFO_HASH_MINI
        {
            public ulong UniqueID { get; set; }
            public string NameCustom { get; set; }
            public string Unit { get; set; }
            public double ValuePrev { get; set; }
            public double ValueNow { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
            public int IndexOrder { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
            public string ReadingType { get; set; }
        }

        private class ShmConnection
        {
            public int RemoteIndex;     // -1 = local, 0+ = remote
            public string ShmPath = string.Empty;
            public MemoryMappedFile? MemMap;
            public MemoryMappedViewAccessor? MemAcc;
            public HWINFO_MEM MemRegion;
            public Dictionary<uint, HWHASH_HEADER> HeaderDict = new();
            public bool IsAlive;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct HWHASH_ELEMENT
        {
            public SENSOR_READING_TYPE SENSOR_TYPE;
            public uint Index;
            public uint ID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
            public string NameDefault;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
            public string NameCustom;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = READING_STRING_LEN)]
            public string Unit;
            public double Value;
            public double ValueMin;
            public double ValueMax;
            public double ValueAvg;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct HWHASH_HEADER
        {
            public uint ID;
            public uint Instance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
            public string NameDefault;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
            public string NameCustom;
        }


        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct HWINFO_MEM
        {
            public uint Sig;
            public uint Ver;
            public uint Rev;
            public long PollTime;
            public uint SS_OFFSET;
            public uint SS_SIZE;
            public uint SS_SensorElements;
            public uint OFFSET_Reading;
            public uint SIZE_Reading;
            public uint TOTAL_ReadingElements;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct HWHASH_Sensor
        {
            public uint ID;
            public uint Instance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
            public string NameDefault;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
            public string NameCustom;
        }


        private enum SENSOR_READING_TYPE
        {
            SENSOR_TYPE_NONE,
            SENSOR_TYPE_TEMP,
            SENSOR_TYPE_VOLT,
            SENSOR_TYPE_FAN,
            SENSOR_TYPE_CURRENT,
            SENSOR_TYPE_POWER,
            SENSOR_TYPE_CLOCK,
            SENSOR_TYPE_USAGE,
            SENSOR_TYPE_OTHER,
        }

        private static ulong FastConcat(uint a, uint b)
        {
            if (b < 10U) return 10UL * a + b;
            if (b < 100U) return 100UL * a + b;
            if (b < 1000U) return 1000UL * a + b;
            if (b < 10000U) return 10000UL * a + b;
            if (b < 100000U) return 100000UL * a + b;
            if (b < 1000000U) return 1000000UL * a + b;
            if (b < 10000000U) return 10000000UL * a + b;
            if (b < 100000000U) return 100000000UL * a + b;
            return 1000000000UL * a + b;
        }



        private static class WinApi
        {
            /// <summary>TimeBeginPeriod(). See the Windows API documentation for details.</summary>

            [SuppressUnmanagedCodeSecurity]
            [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]

            public static extern uint TimeBeginPeriod(uint uMilliseconds);

            /// <summary>TimeEndPeriod(). See the Windows API documentation for details.</summary>

            [SuppressUnmanagedCodeSecurity]
            [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]

            public static extern uint TimeEndPeriod(uint uMilliseconds);
        }



    }
