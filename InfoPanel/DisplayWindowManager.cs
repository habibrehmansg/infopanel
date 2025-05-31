using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel
{
    public class DisplayWindowManager
    {
        private readonly Dictionary<Guid, DisplayWindowThread> DisplayThreads = new();

        public void ShowDisplayWindow(Profile profile)
        {
            var thread = GetDisplayThread(profile.Guid);

            if (thread != null && thread.Direct2DMode != profile.Direct2DMode)
            {
                thread.Close();
                thread = null;
            }

            if (thread == null)
            {
                thread = new DisplayWindowThread(profile);
                DisplayThreads[profile.Guid] = thread;
                thread.Start();
            }

            thread?.Show();
        }

        public void CloseDisplayWindow(Guid profileGuid)
        {
            var thread = GetDisplayThread(profileGuid);
            if (thread != null)
            {
                thread.Close();
                DisplayThreads.Remove(profileGuid);
            }
        }

        public DisplayWindowThread? GetDisplayThread(Guid profileGuid)
        {
            DisplayThreads.TryGetValue(profileGuid, out var thread);
            return thread;
        }

        public void CloseAll()
        {
            foreach (var thread in DisplayThreads.Values.ToList())
            {
                thread.Close();
            }
            DisplayThreads.Clear();
        }
    }
}
