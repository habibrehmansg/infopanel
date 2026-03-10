namespace InfoPanel.Plugins
{
    public class PluginSensor(string id, string name, float value, string? unit = null) : IPluginSensor
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        private float _value = value;
        private readonly Queue<float> _samples = new(60);
        public int SampleWindow { get; set; } = 60;

        public float Value
        {
            get => _value;
            set
            {
                _value = value;
                _samples.Enqueue(value);

                if (_samples.Count > SampleWindow)
                    _samples.Dequeue();

                if (value < ValueMin)
                {
                    ValueMin = value;
                }
                else if (value > ValueMax)
                {
                    ValueMax = value;
                }

                ValueAvg = _samples.Average();
            }
        }

        public float ValueMin { get; private set; } = value;
        public float ValueMax { get; private set; } = value;
        public float ValueAvg { get; private set; } = value;

        public string? Unit { get; } = unit;

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
