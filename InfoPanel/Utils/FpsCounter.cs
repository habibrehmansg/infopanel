using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace InfoPanel.Utils
{
    public class FpsCounter
    {
        public int FramesPerSecond { get; private set; } = 0;
        public long FrameTime { get; private set; } = 0;

        private readonly Stopwatch _stopwatch = new();
        private int _frameCounter = 0;
        private Queue<long> _frameTimeQueue = new(10);
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

        public void Update(long frameTime = 0)
        {
            _frameCounter++;
            var elapsedSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
            
            if (elapsedSeconds >= UpdateInterval)
            {
                FramesPerSecond = Math.Clamp((int)(_frameCounter / elapsedSeconds),1, _maxFrames);

                if (frameTime <= 0)
                {
                    FrameTime = (int)(elapsedSeconds / _frameCounter * 1000);
                }
                _frameCounter = 0;
                _stopwatch.Restart(); // resets and starts the stopwatch
            }

            if(frameTime > 0)
            {
                if(_frameTimeQueue.Count > 10)
                {
                    _frameTimeQueue.Dequeue();
                }
                _frameTimeQueue.Enqueue(frameTime);
                FrameTime = (long)_frameTimeQueue.Average();
            }
        }
    }
}
