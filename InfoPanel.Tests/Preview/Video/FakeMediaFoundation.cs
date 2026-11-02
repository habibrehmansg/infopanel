using System.Collections.Concurrent;
using InfoPanel.Preview.Video;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// A call the fake saw, in order across the catalog: its port (-1: the catalog), name, detail, thread, and when
/// (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>, taken in order with the sequence number).
/// </summary>
internal sealed record FakeMfCall(long Seq, int Port, string Name, string Detail, int Thread, long At);

/// <summary>
/// Media Foundation's hardware encoders as a script, beneath <see cref="IMfCatalog"/> and <see cref="IMfEncoderPort"/>:
/// entries with attributes (identities, vendor ids), each a <see cref="FakeEncoder"/> whose ports answer HRESULTs per
/// stage, send events, and "encode" each input into an Annex B unit of made-up slices around a real SPS and PPS. Every
/// call is recorded with its thread; activated transforms, input buffers and our output samples are counted while live.
/// Behaviour is scripted, never a vendor's: no fake reads a vendor id.
/// </summary>
internal sealed class FakeMfCatalog : IMfCatalog
{
    private readonly Lock _lock = new();
    private readonly List<FakeMfCall> _calls = [];
    private readonly List<FakeMfPort> _ports = [];
    private long _seq;
    private int _live;

    /// <summary>What <see cref="Enumerate"/> lists, in order (a fresh list each call).</summary>
    public List<FakeMfEntry> Entries { get; } = [];

    /// <summary>Media Foundation missing (Windows N): <see cref="Startup"/> throws <see cref="DllNotFoundException"/>.</summary>
    public bool Missing { get; set; }

    public int EnumerateHr { get; set; }

    /// <summary>What <see cref="Startup"/> returns (MFStartup's HRESULT).</summary>
    public int StartupHr { get; set; }

    /// <summary>A thread's COM init by role ("probe", "session", "events", "teardown"): its HRESULT.</summary>
    public Dictionary<string, int> EnterHr { get; } = [];

    /// <summary>A thread's COM init by role waits for this (a wedged init).</summary>
    public Dictionary<string, ManualResetEventSlim> HoldEnter { get; } = [];

    public List<uint> EnumerateFlags { get; } = [];

    public int Startups;

    /// <summary>Activated transforms, input buffers and our output samples not yet released.</summary>
    public int Live => Volatile.Read(ref _live);

    public IReadOnlyList<FakeMfCall> Calls
    {
        get
        {
            lock (_lock)
                return [.. _calls];
        }
    }

    public IReadOnlyList<FakeMfPort> Ports
    {
        get
        {
            lock (_lock)
                return [.. _ports];
        }
    }

    public FakeMfEntry Add(string name, string? vendor = "VEN_1002", Guid? clsid = null, string? url = null, string? deviceKey = null)
    {
        var entry = new FakeMfEntry(this, name);
        entry.Attributes[Mf.FriendlyName] = name;
        if (vendor != null)
            entry.Attributes[Mf.HardwareVendorId] = vendor;
        entry.Attributes[Mf.TransformClsid] = clsid ?? new Guid("ADC9BC80-0F41-46C6-AB75-D693D793597D");
        entry.Attributes[Mf.HardwareUrl] = url ?? "AMDh264Encoder";
        if (deviceKey != null)
            entry.Attributes[Mf.DeviceInstance] = deviceKey;
        // The entry's own value under the driver version's GUID, which isn't the driver's (18.10.0.0): read from the transform only.
        entry.Attributes[Mf.DriverVersion] = 0x0012_000A_0000_0000UL;
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Records a marker in the call order (the test's own moment, as an abort); returns its sequence number.</summary>
    public long Mark(string name) => Record(-1, "Mark", name);

    internal long Record(int port, string name, string detail = "")
    {
        lock (_lock)
        {
            var seq = ++_seq;
            _calls.Add(new FakeMfCall(seq, port, name, detail, Environment.CurrentManagedThreadId, System.Diagnostics.Stopwatch.GetTimestamp()));
            return seq;
        }
    }

    internal void AddLive(int delta) => Interlocked.Add(ref _live, delta);

    internal int NewPort(FakeMfPort port)
    {
        lock (_lock)
        {
            _ports.Add(port);
            return _ports.Count;
        }
    }

    public int EnterThread(string role)
    {
        Record(-1, "Enter", role);
        if (HoldEnter.TryGetValue(role, out var hold))
            hold.Wait();
        return EnterHr.GetValueOrDefault(role);
    }

    public void LeaveThread() => Record(-1, "Leave");

    public int Startup()
    {
        Interlocked.Increment(ref Startups);
        if (Missing)
            throw new DllNotFoundException("mfplat.dll");
        return StartupHr;
    }

    public int Enumerate(uint flags, out IReadOnlyList<IMfEntry> entries)
    {
        Record(-1, "Enumerate", $"0x{flags:X}");
        lock (_lock)
            EnumerateFlags.Add(flags);
        entries = EnumerateHr < 0 ? [] : [.. Entries];
        return EnumerateHr;
    }

    /// <summary>Names of the calls after <paramref name="mark"/> (a sequence number).</summary>
    public IEnumerable<FakeMfCall> After(long mark) => Calls.Where(c => c.Seq > mark);
}

/// <summary>A slot of the graphics chip's encoder: a begin past its sessions answers <see cref="Mf.HwMftFailedStartStreaming"/>.</summary>
internal sealed class FakeMfDevice(int sessions)
{
    private int _used;

    public int Used => Volatile.Read(ref _used);

    public bool TryTake()
    {
        while (true)
        {
            var used = Volatile.Read(ref _used);
            if (used >= sessions)
                return false;
            if (Interlocked.CompareExchange(ref _used, used + 1, used) == used)
                return true;
        }
    }

    public void Free() => Interlocked.Decrement(ref _used);
}

internal sealed class FakeMfEntry(FakeMfCatalog catalog, string name) : IMfEntry
{
    private readonly List<FakeMfPort> _ports = [];

    public string Name { get; } = name;

    public Dictionary<Guid, object> Attributes { get; } = [];

    /// <summary>The behaviour of every transform activated from this entry (changeable between starts).</summary>
    public FakeEncoder Encoder { get; set; } = new();

    public IReadOnlyList<FakeMfPort> Ports
    {
        get
        {
            lock (_ports)
                return [.. _ports];
        }
    }

    public FakeMfPort Last => Ports[^1];

    /// <summary>Ports that were activated successfully.</summary>
    public int Activations => Ports.Count(p => p.Activated);

    /// <summary>Ports that ran three probe-sized frames (322 × 194).</summary>
    public int Probes => Ports.Count(p => p.Output?.Width == MediaFoundationH264.ProbeWidth && p.Output?.Height == MediaFoundationH264.ProbeHeight);

    public string? GetString(Guid key) => Attributes.GetValueOrDefault(key) as string;

    public Guid? GetGuid(Guid key) => Attributes.GetValueOrDefault(key) is Guid g ? g : null;

    public ulong? GetUInt64(Guid key) => Attributes.GetValueOrDefault(key) is ulong v ? v : null;

    public IMfEncoderPort CreatePort()
    {
        var port = new FakeMfPort(catalog, this, Encoder);
        lock (_ports)
            _ports.Add(port);
        return port;
    }

    public void Dispose()
    {
    }

    public override string ToString() => Name;
}

/// <summary>What a fake encoder does, by behaviour; every member's default is what the AMD encoder showed.</summary>
internal sealed class FakeEncoder
{
    /// <summary>Activation waits for this (a slow or hung activation).</summary>
    public ManualResetEventSlim? HoldActivate;

    /// <summary><c>MF_TRANSFORM_ASYNC</c> on the transform (null: absent, so not asynchronous).</summary>
    public uint? Async = 1;

    /// <summary>The transform's driver version as a UINT64 (null: absent). 32.0.21030.2001.</summary>
    public ulong? DriverVersion = 0x0020_0000_5226_07D1;

    /// <summary>The transform's driver version as a string, the type mfapi.h declares (null: absent).</summary>
    public string? DriverVersionText;

    /// <summary>The first ProcessOutput calls with an output ready answer events and no sample (S_FALSE), this many, each followed by another HaveOutput.</summary>
    public int NoSampleAnswers;

    public Dictionary<Guid, int> RefuseAttribute { get; } = [];

    public Dictionary<Guid, int> RefuseCodec { get; } = [];

    /// <summary>What <c>GetValue</c> answers for a property (absent: what was set).</summary>
    public Dictionary<Guid, int> CodecGetHr { get; } = [];

    public Dictionary<Guid, uint> CodecReadBack { get; } = [];

    /// <summary>Refuses output types with this (a transform that needs a device manager, a size it can't take).</summary>
    public Func<MfVideoType, int> OutputTypeHr = _ => Mf.SOk;

    public Func<MfVideoType, int> InputTypeHr = _ => Mf.SOk;

    /// <summary>The profile the current output type reads back (null: as set).</summary>
    public uint? ReadBackProfile;

    /// <summary>The input type's layout as it reads back (null: its own size and stride, no sample size).</summary>
    public MfInputLayout? InputLayout;

    /// <summary>The input stream info: AMD's flags 0xB, four bytes a pixel as its size, no alignment.</summary>
    public Func<int, int, MfStreamInfo> InputInfo = (w, h) => new MfStreamInfo(0xB, w * h * 4, 0);

    /// <summary>The output stream info: AMD's, the encoder provides its samples (0x103).</summary>
    public MfStreamInfo OutputInfo = new(0x103, 0, 0);

    public int BeginHr;

    /// <summary>
    /// A failing HRESULT for a call by name: <c>Activate</c>, <c>GetOutputCurrentType</c>, <c>GetInputCurrentLayout</c>,
    /// <c>GetInputStreamInfo</c>, <c>GetOutputStreamInfo</c>, <c>START_OF_STREAM</c>.
    /// </summary>
    public Dictionary<string, int> CallHr { get; } = [];

    /// <summary>The chip's sessions, shared by every port of entries given the same device.</summary>
    public FakeMfDevice? Device;

    /// <summary>The next begins (this many) each take their slot, then wait until all of them have (two starts' begins at once).</summary>
    public int BeginsTogether;

    public Barrier? BeginBarrier;

    /// <summary><c>NeedInput</c> credits at the start of the stream, then one per output taken.</summary>
    public int InitialCredits = 1;

    public bool CreditPerOutput = true;

    /// <summary>Outputs are made but held back until <see cref="FakeMfPort.ReleaseOutputs"/>.</summary>
    public bool HoldOutputs;

    /// <summary>Each output comes only with the next input (a held frame).</summary>
    public bool HoldUntilNextInput;

    public bool FirstDelta;

    public bool IgnoreForce;

    /// <summary>This input (0-based) comes out an IDR of the encoder's own accord, forced or not; -1: none.</summary>
    public int KeyAt = -1;

    public bool IdrWithoutParameterSets;

    /// <summary>The parameter sets only in the output type's sequence header, none in band.</summary>
    public bool ParameterSetsOnlyInHeader;

    /// <summary>The coded picture its SPS describes (null: the session's), and from which output on.</summary>
    public (int Width, int Height)? SpsSize;

    public int SpsSizeFrom;

    public byte SpsProfile = 66;

    /// <summary>In-band parameter sets of its own (null: made up for the session's size).</summary>
    public byte[]? Sps;

    public byte[]? Pps;

    /// <summary>The sequence header its output type offers (<c>MF_MT_MPEG_SEQUENCE_HEADER</c>).</summary>
    public byte[]? OfferedHeader;

    /// <summary>The output of this input (0-based) carries another time; -1: none.</summary>
    public int WrongTimeAt = -1;

    public bool NoTime;

    /// <summary>An output with no input behind it, after the first.</summary>
    public bool ExtraOutput;

    /// <summary><c>ProcessOutput</c> waits for this (or the shutdown).</summary>
    public ManualResetEventSlim? HoldProcessOutput;

    public ManualResetEventSlim? HoldShutdown;

    /// <summary>The force (<c>ICodecAPI</c>) waits for this.</summary>
    public ManualResetEventSlim? HoldForce;

    /// <summary>After this many outputs, <c>MEError</c>; -1: never.</summary>
    public int ErrorAfter = -1;

    /// <summary>Events of other types sent at the start of the stream.</summary>
    public List<uint> ExtraEvents { get; } = [];

    /// <summary>The first ProcessOutput calls answer a stream change, this many in a row.</summary>
    public int StreamChanges;

    /// <summary>The type available after a stream change (null: H.264 Baseline at the session's size).</summary>
    public Func<int, int, MfVideoType>? ChangeTo;

    /// <summary>The output stream info after a stream change (null: unchanged).</summary>
    public MfStreamInfo? ChangeInfo;
}

internal sealed class FakeMfPort : IMfEncoderPort
{
    private static readonly byte[] Aud = [0x09, 0xF0];

    private readonly FakeMfCatalog _catalog;
    private readonly FakeEncoder _e;
    private readonly BlockingCollection<MfEvent> _events = new();
    private readonly Lock _lock = new();
    private readonly Queue<MfOutput> _ready = new();
    private readonly Queue<MfOutput> _held = new();
    private readonly Dictionary<Guid, uint> _codec = [];
    private readonly ManualResetEventSlim _shut = new();
    private int _credits;
    private int _inputs;
    private int _outputs;
    private bool _forcePending;
    private bool _deviceSlot;
    private bool _awaitingType;
    private int _changesLeft;
    private int _noSampleLeft;
    private MfStreamInfo _outputInfo;

    public FakeMfPort(FakeMfCatalog catalog, FakeMfEntry entry, FakeEncoder encoder)
    {
        _catalog = catalog;
        _e = encoder;
        Entry = entry;
        _changesLeft = encoder.StreamChanges;
        _noSampleLeft = encoder.NoSampleAnswers;
        _outputInfo = encoder.OutputInfo;
        Id = catalog.NewPort(this);
    }

    public int Id { get; }

    public FakeMfEntry Entry { get; }

    public bool Activated { get; private set; }

    public bool Configured { get; private set; }

    public bool ShutDown { get; private set; }

    public bool Released { get; private set; }

    public MfVideoType? Output { get; private set; }

    public MfVideoType? Input { get; private set; }

    /// <summary>Inputs taken without a credit (refused with <see cref="Mf.NotAccepting"/>).</summary>
    public int InputsWithoutCredit;

    /// <summary>ProcessOutput calls made with an allocation other than the current stream info's, or before the new type after a change.</summary>
    public int StaleAllocations;

    public List<(int Size, int Alignment)> InputBuffers { get; } = [];

    /// <summary>What each input sample's buffer held, and its current length.</summary>
    public List<byte[]> InputBytes { get; } = [];

    public List<long> InputTimes { get; } = [];

    /// <summary>Our output samples ProcessOutput was handed: their size and alignment.</summary>
    public List<(int Size, int Alignment)> OursMade { get; } = [];

    public Dictionary<Guid, uint> CodecSet => _codec;

    public int Outputs => Volatile.Read(ref _outputs);

    private void Record(string name, string detail = "") => _catalog.Record(Id, name, detail);

    // ---- what a test sends ----

    public void Send(MfEvent e)
    {
        if (!_events.IsAddingCompleted)
        {
            try
            {
                _events.Add(e);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public void SendNeedInput(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Interlocked.Increment(ref _credits);
            Send(new MfEvent(Mf.EventNeedInput, 0));
        }
    }

    /// <summary>Sends <c>HaveOutput</c> for up to <paramref name="count"/> held outputs.</summary>
    public void ReleaseOutputs(int count = int.MaxValue)
    {
        while (count-- > 0)
        {
            lock (_lock)
            {
                if (!_held.TryDequeue(out var output))
                    return;
                _ready.Enqueue(output);
            }

            Send(new MfEvent(Mf.EventHaveOutput, 0));
        }
    }

    // ---- IMfEncoderPort ----

    public int Activate()
    {
        Record("Activate");
        _e.HoldActivate?.Wait();
        if (_e.CallHr.GetValueOrDefault("Activate") is var refused and < 0)
            return refused;
        Activated = true;
        _catalog.AddLive(1);
        return Mf.SOk;
    }

    public int GetUInt32(Guid key, out uint value)
    {
        Record("GetUInt32", Name(key));
        value = 0;
        if (key == Mf.TransformAsync && _e.Async is { } isAsync)
        {
            value = isAsync;
            return Mf.SOk;
        }

        return unchecked((int)0xC00D36E6); // MF_E_ATTRIBUTENOTFOUND
    }

    public int GetUInt64(Guid key, out ulong value)
    {
        Record("GetUInt64", Name(key));
        value = 0;
        if (key == Mf.DriverVersion && _e.DriverVersion is { } version)
        {
            value = version;
            return Mf.SOk;
        }

        return unchecked((int)0xC00D36E6);
    }

    public int GetString(Guid key, out string? value)
    {
        Record("GetString", Name(key));
        value = null;
        if (key == Mf.DriverVersion && _e.DriverVersionText is { } text)
        {
            value = text;
            return Mf.SOk;
        }

        return unchecked((int)0xC00D36E6);
    }

    public int SetUInt32(Guid key, uint value)
    {
        Record("SetUInt32", Name(key));
        Configured = true;
        return _e.RefuseAttribute.GetValueOrDefault(key);
    }

    public int SetCodecValue(Guid api, uint value)
    {
        Record("SetCodecValue", $"{Name(api)}={value}");
        Configured = true;
        if (api == Mf.CodecForceKeyFrame)
            _e.HoldForce?.Wait();
        if (_e.RefuseCodec.GetValueOrDefault(api) is var hr and < 0)
            return hr;
        lock (_lock)
        {
            _codec[api] = value;
            if (api == Mf.CodecForceKeyFrame && value != 0)
                _forcePending = true;
        }

        return Mf.SOk;
    }

    public int GetCodecValue(Guid api, out uint value)
    {
        Record("GetCodecValue", Name(api));
        value = 0;
        if (_e.CodecGetHr.TryGetValue(api, out var hr))
            return hr;
        lock (_lock)
            value = _e.CodecReadBack.TryGetValue(api, out var v) ? v : _codec.GetValueOrDefault(api);
        return Mf.SOk;
    }

    public int SetOutputType(MfVideoType type)
    {
        Record("SetOutputType", type.ToString());
        Configured = true;
        var hr = _e.OutputTypeHr(type);
        if (hr < 0)
            return hr;
        Output = type;
        _awaitingType = false;
        return Mf.SOk;
    }

    public int SetInputType(MfVideoType type)
    {
        Record("SetInputType", type.ToString());
        Configured = true;
        // An encoder takes its input type only after its output type.
        if (Output == null)
            return Mf.TransformTypeNotSet;
        var hr = _e.InputTypeHr(type);
        if (hr < 0)
            return hr;
        Input = type;
        return Mf.SOk;
    }

    public int GetOutputCurrentType(out MfVideoType? type)
    {
        Record("GetOutputCurrentType");
        if (_e.CallHr.GetValueOrDefault("GetOutputCurrentType") is var refused and < 0)
        {
            type = null;
            return refused;
        }

        type = Output == null ? null : Output with { Profile = _e.ReadBackProfile ?? Output.Profile, SequenceHeader = _e.OfferedHeader };
        return type == null ? Mf.TransformTypeNotSet : Mf.SOk;
    }

    public int GetOutputAvailableType(int index, out MfVideoType? type)
    {
        Record("GetOutputAvailableType", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var w = Output?.Width ?? 0;
        var h = Output?.Height ?? 0;
        type = (_e.ChangeTo?.Invoke(w, h) ?? new MfVideoType(Mf.H264, w, h) { Profile = Mf.ProfileBaseline }) with { SequenceHeader = _e.OfferedHeader };
        return Mf.SOk;
    }

    public int GetInputCurrentLayout(out MfInputLayout layout)
    {
        Record("GetInputCurrentLayout");
        if (_e.CallHr.GetValueOrDefault("GetInputCurrentLayout") is var refused and < 0)
        {
            layout = default;
            return refused;
        }

        layout =_e.InputLayout ?? new MfInputLayout(Input?.Width ?? 0, Input?.Height ?? 0, Input?.DefaultStride, null);
        return Input == null ? Mf.TransformTypeNotSet : Mf.SOk;
    }

    public int GetInputStreamInfo(out MfStreamInfo info)
    {
        Record("GetInputStreamInfo");
        info = _e.InputInfo(Input?.Width ?? 0, Input?.Height ?? 0);
        return Math.Min(_e.CallHr.GetValueOrDefault("GetInputStreamInfo"), Mf.SOk);
    }

    public int GetOutputStreamInfo(out MfStreamInfo info)
    {
        Record("GetOutputStreamInfo");
        lock (_lock)
            info = _outputInfo;
        return Math.Min(_e.CallHr.GetValueOrDefault("GetOutputStreamInfo"), Mf.SOk);
    }

    public int ProcessMessage(uint message)
    {
        Record("ProcessMessage", MessageName(message));
        if (message == Mf.MessageNotifyBeginStreaming)
        {
            var hr = _e.BeginHr;
            if (hr >= 0 && _e.Device is { } device)
            {
                if (device.TryTake())
                    _deviceSlot = true;
                else
                    hr = Mf.HwMftFailedStartStreaming;
            }

            if (_e.BeginBarrier is { } together && Interlocked.Decrement(ref _e.BeginsTogether) >= 0)
                together.SignalAndWait();
            return hr;
        }
        else if (message == Mf.MessageNotifyStartOfStream)
        {
            if (_e.CallHr.GetValueOrDefault("START_OF_STREAM") is var refused and < 0)
                return refused;
            for (var i = 0; i < _e.InitialCredits; i++)
                SendNeedInput();
            foreach (var type in _e.ExtraEvents)
                Send(new MfEvent(type, 0));
        }

        return Mf.SOk;
    }

    public int NextEvent(out MfEvent e)
    {
        try
        {
            e = _events.Take();
            return Mf.SOk;
        }
        catch (InvalidOperationException)
        {
            e = default;
            return Mf.Shutdown;
        }
    }

    public int CreateInputBuffer(int size, int alignment, out IMfInputBuffer? buffer)
    {
        Record("CreateInputBuffer", $"{size}/{alignment}");
        lock (_lock)
            InputBuffers.Add((size, alignment));
        _catalog.AddLive(1);
        buffer = new FakeMfInputBuffer(_catalog, size);
        return Mf.SOk;
    }

    public int ProcessInput(IMfInputBuffer buffer, long sampleTime, long duration)
    {
        Record("ProcessInput", sampleTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (ShutDown)
            return Mf.Shutdown;
        if (Interlocked.Decrement(ref _credits) < 0)
        {
            Interlocked.Increment(ref _credits);
            Interlocked.Increment(ref InputsWithoutCredit);
            return Mf.NotAccepting;
        }

        var input = (FakeMfInputBuffer)buffer;
        bool key;
        int index;
        lock (_lock)
        {
            InputBytes.Add(input.Bytes[..input.Length]);
            InputTimes.Add(sampleTime);
            index = _inputs++;
            // The first frame is an IDR (or, scripted, a delta) forced or not; a later one is an IDR when forced and honoured.
            key = index == 0 ? !_e.FirstDelta : (_forcePending && !_e.IgnoreForce) || index == _e.KeyAt;
            _forcePending = false;
        }

        var time = index == _e.WrongTimeAt ? sampleTime + 1 : sampleTime;
        var output = new MfOutput(Unit(key, index), _e.NoTime ? null : time, key);
        var release = new List<MfOutput>();
        lock (_lock)
        {
            if (_e.HoldUntilNextInput)
            {
                while (_held.TryDequeue(out var previous))
                    release.Add(previous);
                _held.Enqueue(output);
            }
            else if (_e.HoldOutputs)
            {
                _held.Enqueue(output);
            }
            else
            {
                release.Add(output);
            }

            if (_e.ExtraOutput && index == 0)
                release.Add(output with { SampleTime = time + 1_000_000 });
        }

        foreach (var ready in release)
        {
            lock (_lock)
                _ready.Enqueue(ready);
            Send(new MfEvent(Mf.EventHaveOutput, 0));
        }

        return Mf.SOk;
    }

    public int ProcessOutput(MfOutputAllocation allocation, out MfOutput? output)
    {
        Record("ProcessOutput", allocation.EncoderProvides ? "theirs" : $"ours {allocation.Size}/{allocation.Alignment}");
        output = null;
        var noSample = false;
        if (_e.HoldProcessOutput is { } hold)
            WaitHandle.WaitAny([hold.WaitHandle, _shut.WaitHandle]);
        if (ShutDown)
            return Mf.Shutdown;
        lock (_lock)
        {
            if (_awaitingType)
            {
                StaleAllocations++;
                return Mf.TransformTypeNotSet;
            }

            if (_changesLeft > 0)
            {
                _changesLeft--;
                _awaitingType = true;
                _outputInfo = _e.ChangeInfo ?? _outputInfo;
                return Mf.TransformStreamChange;
            }

            if (allocation != MfOutputAllocation.Of(_outputInfo))
            {
                StaleAllocations++;
                return Mf.EInvalidArg;
            }

            if (_ready.Count > 0 && _noSampleLeft > 0)
            {
                _noSampleLeft--;
                noSample = true;
            }
            else if (!_ready.TryDequeue(out var ready))
            {
                return Mf.TransformNeedMoreInput;
            }
            else
            {
                output = ready;
                _outputs++;
            }
        }

        if (noSample)
        {
            // Events and no sample: the output follows on the next HaveOutput.
            Send(new MfEvent(Mf.EventHaveOutput, 0));
            return Mf.SFalse;
        }

        if (!allocation.EncoderProvides)
        {
            // One of ours, made for the call and released after the copy.
            lock (_lock)
                OursMade.Add((allocation.Size, allocation.Alignment));
            _catalog.AddLive(1);
            _catalog.AddLive(-1);
        }

        if (_e.CreditPerOutput)
            SendNeedInput();
        if (_e.ErrorAfter >= 0 && Outputs == _e.ErrorAfter)
            Send(new MfEvent(Mf.EventError, Mf.EFail));
        return Mf.SOk;
    }

    public int Shutdown()
    {
        Record("Shutdown");
        _e.HoldShutdown?.Wait();
        ShutDown = true;
        _shut.Set();
        _events.CompleteAdding();
        if (_deviceSlot)
        {
            _deviceSlot = false;
            _e.Device!.Free();
        }

        return Mf.SOk;
    }

    public void Release()
    {
        Record("Release");
        if (Released)
            return;
        Released = true;
        if (Activated)
            _catalog.AddLive(-1);
        if (_deviceSlot)
        {
            _deviceSlot = false;
            _e.Device!.Free();
        }
    }

    // ---- the "encoded" units ----

    /// <summary>An access unit: AUD, then on a keyframe the SPS and PPS (unless scripted away), then one slice.</summary>
    private byte[] Unit(bool key, int index)
    {
        var w = Output?.Width ?? 0;
        var h = Output?.Height ?? 0;
        var nals = new List<byte[]> { Aud };
        if (key && !_e.IdrWithoutParameterSets && !_e.ParameterSetsOnlyInHeader)
        {
            var size = index >= _e.SpsSizeFrom && _e.SpsSize is { } s ? s : (w, h);
            nals.Add(_e.Sps ?? SpsFor(size.Item1, size.Item2, _e.SpsProfile));
            nals.Add(_e.Pps ?? MadeUpH264.Pps(0, 0));
        }

        nals.Add(MadeUpH264.Slice(key ? 5 : 1, 0, payload: [(byte)index, 0x9A, 0x30, 0x0C]));
        return [.. nals.SelectMany(n => (byte[])[0, 0, 0, 1, .. n])];
    }

    /// <summary>A Baseline SPS coded in macroblocks and cropped to <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static byte[] SpsFor(int width, int height, byte profile = 66)
    {
        var mbsWide = (width + 15) / 16;
        var mbsHigh = (height + 15) / 16;
        var right = (mbsWide * 16 - width) / 2;
        var bottom = (mbsHigh * 16 - height) / 2;
        return MadeUpH264.Sps(0, mbsWide, mbsHigh, crop: right != 0 || bottom != 0 ? (0, right, 0, bottom) : null, profile: profile);
    }

    private static string Name(Guid key) =>
        key == Mf.TransformAsync ? "ASYNC" :
        key == Mf.TransformAsyncUnlock ? "ASYNC_UNLOCK" :
        key == Mf.LowLatency ? "LOW_LATENCY" :
        key == Mf.DriverVersion ? "DRIVER_VERSION" :
        key == Mf.CodecForceKeyFrame ? "FORCE" :
        key == Mf.CodecBFrames ? "BFRAMES" :
        key == Mf.CodecGopSize ? "GOP" :
        key == Mf.CodecMeanBitRate ? "BITRATE" :
        key == Mf.CodecRateControlMode ? "RATECONTROL" :
        key.ToString();

    public static string MessageName(uint message) => message switch
    {
        Mf.MessageCommandFlush => "FLUSH",
        Mf.MessageCommandDrain => "DRAIN",
        Mf.MessageSetD3DManager => "SET_D3D_MANAGER",
        Mf.MessageNotifyBeginStreaming => "BEGIN_STREAMING",
        Mf.MessageNotifyEndStreaming => "END_STREAMING",
        Mf.MessageNotifyEndOfStream => "END_OF_STREAM",
        Mf.MessageNotifyStartOfStream => "START_OF_STREAM",
        _ => $"0x{message:X}",
    };
}

internal sealed class FakeMfInputBuffer(FakeMfCatalog catalog, int size) : IMfInputBuffer
{
    private int _disposed;

    public byte[] Bytes { get; } = new byte[size];

    public int Length { get; private set; }

    public int Write(ReadOnlySpan<byte> data)
    {
        if (data.Length > Bytes.Length)
            return Mf.EInvalidArg;
        data.CopyTo(Bytes);
        Length = data.Length;
        return Mf.SOk;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            catalog.AddLive(-1);
    }
}
