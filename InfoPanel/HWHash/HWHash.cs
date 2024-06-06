using InfoPanel;
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

    //Initially it was based on zipferot3000 implementation, but he was using NewtonsoftJSON which I avoid, so I modified it to use MsgPack
    //Since most users are not familiar with MsgPack I am using the default serializer without external dependencies and some minor tweaks.


    const string SHARED_MEM_PATH = "Global\\HWiNFO_SENS_SM2";
    const int SENSOR_STRING_LEN = 128;
    const int READING_STRING_LEN = 16;
    private static MemoryMappedFile? MEM_MAP;
    private static MemoryMappedViewAccessor? MEM_ACC;
    private static HWINFO_MEM HWINFO_MEMREGION;

    private static readonly Stopwatch SW = Stopwatch.StartNew();

    private static HWHashStats SelfData;

    private static int IndexOrder = 0;

    private static System.Timers.Timer aTimer = new(1000);

    private static Dictionary<uint, HWHASH_HEADER> HEADER_DICT = new Dictionary<uint, HWHASH_HEADER>();
    public static ConcurrentDictionary<(UInt32, UInt32, UInt32), HWINFO_HASH> SENSORHASH = new ConcurrentDictionary<(UInt32, UInt32, UInt32), HWINFO_HASH>();
    public static ConcurrentDictionary<(UInt32, UInt32, UInt32), HWINFO_HASH_MINI> SENSORHASH_MINI = new ConcurrentDictionary<(UInt32, UInt32, UInt32), HWINFO_HASH_MINI>();
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
        HWINFO_RUNNING = ReadMem();

        if(HWINFO_RUNNING )
        {
            SharedModel.Instance.HWiNFOError = false;
            BuildHeaders();
        } else
        {
            SharedModel.Instance.HWiNFOError = true;
            aTimer.Stop();
            Thread.Sleep(1000);
            aTimer.Start();
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
            if (CheckMem())
            {
                ReadSensors();
            }
            else
            {
                Trace.WriteLine("SHM DEAD");
                HWINFO_RUNNING = false;
                MEM_ACC?.Dispose();
                MEM_ACC = null;
                MEM_MAP?.Dispose();
                MEM_MAP = null;
                HEADER_DICT.Clear();
                SENSORHASH.Clear();
                SENSORHASH_MINI.Clear();
            }
        } else
        {
            Prepare();
        }
    }

    private static bool ReadMem()
    {
        Trace.WriteLine("ReadMem()");
        HWINFO_MEMREGION = new HWINFO_MEM();
        try
        {
            MEM_MAP = MemoryMappedFile.OpenExisting(SHARED_MEM_PATH, MemoryMappedFileRights.Read);
            MEM_ACC = MEM_MAP.CreateViewAccessor(0L, Marshal.SizeOf(typeof(HWINFO_MEM)), MemoryMappedFileAccess.Read);
            MEM_ACC.Read(0L, out HWINFO_MEMREGION);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CheckMem()
    {
        bool result = false;
        try
        {
            if (MEM_ACC != null)
            {
                MEM_ACC.Read(0L, out HWINFO_MEMREGION);
                byte[] bytes = BitConverter.GetBytes(HWINFO_MEMREGION.Sig);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }
                string stringValue = Encoding.UTF8.GetString(bytes);

                result = stringValue != "DEAD";
            }
        } catch (Exception) { }

        return result;
    }

    private static void BuildHeaders()
    {
        Trace.WriteLine("BuildHeaders()");
        if (MEM_MAP != null)
        {
            for (uint index = 0; index < HWINFO_MEMREGION.SS_SensorElements; ++index)
            {
                using (MemoryMappedViewStream viewStream = MEM_MAP.CreateViewStream(HWINFO_MEMREGION.SS_OFFSET + index * HWINFO_MEMREGION.SS_SIZE, HWINFO_MEMREGION.SS_SIZE, MemoryMappedFileAccess.Read))
                {
                    byte[] buffer = new byte[(int)HWINFO_MEMREGION.SS_SIZE];
                    viewStream.Read(buffer, 0, (int)HWINFO_MEMREGION.SS_SIZE);
                    GCHandle gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    HWHASH_HEADER structure = (HWHASH_HEADER)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(HWHASH_HEADER));
                    if(!HEADER_DICT.ContainsKey(index))
                    {
                        HEADER_DICT.Add(index, structure);
                    }
                }
            }
            SelfData.TotalCategories = HWINFO_MEMREGION.SS_SensorElements;
        }
    }

    private static void ReadSensors()
    {
        MiniBenchmark(0);
        SelfData.TotalEntries = HWINFO_MEMREGION.TOTAL_ReadingElements;
        for (uint index = 0; index < HWINFO_MEMREGION.TOTAL_ReadingElements; ++index)
        {
            using (MemoryMappedViewStream viewStream = MEM_MAP.CreateViewStream(HWINFO_MEMREGION.OFFSET_Reading + index * HWINFO_MEMREGION.SIZE_Reading, HWINFO_MEMREGION.SIZE_Reading, MemoryMappedFileAccess.Read))
            {
                byte[] buffer = new byte[(int)HWINFO_MEMREGION.SIZE_Reading];
                viewStream.Read(buffer, 0, (int)HWINFO_MEMREGION.SIZE_Reading);
                GCHandle gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                HWHASH_ELEMENT structure = (HWHASH_ELEMENT)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(HWHASH_ELEMENT));
                FormatSensor(structure);
                gcHandle.Free();
            }
        }
        MiniBenchmark(1);
    }

    private static void FormatSensor(HWHASH_ELEMENT READING)
    {
        var KEY = (HEADER_DICT[READING.Index].ID, HEADER_DICT[READING.Index].Instance, READING.ID);
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
                ParentNameDefault = HEADER_DICT[READING.Index].NameDefault,
                ParentNameCustom = HEADER_DICT[READING.Index].NameCustom,
                ParentID = HEADER_DICT[READING.Index].ID,
                ParentInstance = HEADER_DICT[READING.Index].Instance,
                ParentUniqueID = FastConcat(HEADER_DICT[READING.Index].ID, HEADER_DICT[READING.Index].Instance),
                IndexOrder = IndexOrder++

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
    /// Returns a list respecting the same order as HWInfo original user interface, in a minified version.
    /// </summary>
    public static List<HWINFO_HASH_MINI> GetOrderedListMini()
    {
        List<HWINFO_HASH_MINI> OrderedList = SENSORHASH_MINI.Values.OrderBy(x => x.IndexOrder).ToList();
        return OrderedList;
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
