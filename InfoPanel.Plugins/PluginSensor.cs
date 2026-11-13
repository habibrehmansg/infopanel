namespace InfoPanel.Plugins
{
    /// <summary>
    /// A number a plugin reads, with its minimum, maximum and average over its last <see cref="SampleWindow"/> values. A
    /// value that isn't finite (set <c>float.NaN</c> when the plugin doesn't know the number) is the sensor's value, which
    /// InfoPanel shows as unavailable ("–"), but is left out of the minimum, maximum and average. Values may be set and
    /// read from any thread; InfoPanel reads a value with the minimum, maximum and average set with it.
    /// </summary>
    public class PluginSensor(string id, string name, float value, string? unit = null) : IPluginSensor
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        // Guards the value, the samples and the statistics, so a reader never sees a value beside another's statistics.
        private readonly object _lock = new();
        private float _value = value;
        private float _min = value;
        private float _max = value;
        private float _avg = value;
        private readonly Queue<float> _samples = new(60);
        public int SampleWindow { get; set; } = 60;

        public float Value
        {
            get
            {
                lock (_lock)
                    return _value;
            }
            set
            {
                lock (_lock)
                {
                    _value = value;
                    if (!float.IsFinite(value))
                        return;

                    _samples.Enqueue(value);

                    while (_samples.Count > Math.Max(SampleWindow, 1))
                        _samples.Dequeue();

                    // A minimum or maximum that isn't finite is one the sensor was made with: the first finite value replaces it.
                    if (value < _min || !float.IsFinite(_min))
                    {
                        _min = value;
                    }
                    if (value > _max || !float.IsFinite(_max))
                    {
                        _max = value;
                    }

                    _avg = _samples.Average();
                }
            }
        }

        public float ValueMin
        {
            get
            {
                lock (_lock)
                    return _min;
            }
        }

        public float ValueMax
        {
            get
            {
                lock (_lock)
                    return _max;
            }
        }

        public float ValueAvg
        {
            get
            {
                lock (_lock)
                    return _avg;
            }
        }

        /// <summary>The value with the minimum, maximum and average set with it, read at once.</summary>
        internal PluginSensorReading Read()
        {
            lock (_lock)
                return new PluginSensorReading(_value, _min, _max, _avg);
        }

        public string? Unit { get; set; } = unit;

        public PluginSensor(string name, float value, string? unit = null) : this(IdUtil.Encode(name), name, value, unit)
        {
        }

        public override string ToString()
        {
            if (Unit == "%" && Math.Round(Value, 1) == 100)
            {
                return "100%";
            }
            else if (Unit == "%" && Math.Round(Value, 1) == 0)
            {
                return "0%";
            }

            return $"{Math.Round(Value, 1):F1}{Unit}";
        }
    }
}
