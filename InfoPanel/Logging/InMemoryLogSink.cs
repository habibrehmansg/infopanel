using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace InfoPanel.Logging
{
    public sealed class InMemoryLogSink : ILogEventSink
    {
        private static readonly Lazy<InMemoryLogSink> _instance = new(() => new InMemoryLogSink());
        public static InMemoryLogSink Instance => _instance.Value;

        private const int MaxLines = 2000;
        private readonly ConcurrentQueue<string> _lines = new();
        private int _count;

        private readonly MessageTemplateTextFormatter _formatter = new(
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] [{ThreadName}] - [{SourceContext}] {Message:lj}{NewLine}{Exception}");

        public event Action<string>? LogReceived;

        private InMemoryLogSink() { }

        public void Emit(LogEvent logEvent)
        {
            using var writer = new StringWriter();
            _formatter.Format(logEvent, writer);
            var line = writer.ToString().TrimEnd('\r', '\n');

            _lines.Enqueue(line);
            var count = System.Threading.Interlocked.Increment(ref _count);

            while (count > MaxLines && _lines.TryDequeue(out _))
            {
                System.Threading.Interlocked.Decrement(ref _count);
                count = _count;
            }

            LogReceived?.Invoke(line);
        }

        public string GetLogs()
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }
}
