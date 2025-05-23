using System;
using System.Diagnostics;

namespace InfoPanel.Utils
{
    public class FpsCounter
    {
        public int FramesPerSecond { get; private set; }

        private readonly Stopwatch _stopwatch = new();
        private int _frameCounter = 0;
        private int _maxFrames;
        private const float UpdateInterval = 0.5f; // 0.5 seconds

        public FpsCounter(int maxFrames = 60)
        {
            _stopwatch.Start();
            _maxFrames = maxFrames;
        }

        public void SetMaxFrames(int maxFrames)
        {
            _maxFrames = maxFrames;
        }

        public void Update()
        {
            _frameCounter++;
            var elapsedSeconds = (float)_stopwatch.Elapsed.TotalSeconds;

            if (elapsedSeconds >= UpdateInterval)
            {
                FramesPerSecond = Math.Clamp((int)(_frameCounter / elapsedSeconds),1, _maxFrames);
                _frameCounter = 0;
                _stopwatch.Restart(); // resets and starts the stopwatch
            }
        }
    }
}
