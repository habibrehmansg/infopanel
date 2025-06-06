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
        private readonly Queue<long> _frameTimeQueue;
        private int _maxFrames;
        private const float UpdateInterval = 0.5f; // 0.5 seconds

        public FpsCounter(int maxFrames = 60)
        {
            _stopwatch.Start();
            _maxFrames = maxFrames;

            _frameTimeQueue = new(maxFrames);
        }

        public void SetMaxFrames(int maxFrames)
        {
            _maxFrames = maxFrames;
        }

        public void Update(long? frameTime = null)
        {
            _frameCounter++;
            var elapsedSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
            
            if (elapsedSeconds >= UpdateInterval)
            {
                FramesPerSecond = Math.Clamp((int)(_frameCounter / elapsedSeconds),1, _maxFrames);

                if (frameTime == null)
                {
                    FrameTime = (int)(elapsedSeconds / _frameCounter * 1000);
                }
                _frameCounter = 0;
                _stopwatch.Restart(); // resets and starts the stopwatch
            }

            if(frameTime != null)
            {
                _frameTimeQueue.Enqueue(frameTime.Value);
                if (_frameTimeQueue.Count > _maxFrames)
                {
                    _frameTimeQueue.Dequeue();
                }
                FrameTime = (long)_frameTimeQueue.Average();
            }
        }
    }
}
