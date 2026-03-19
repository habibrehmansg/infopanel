using InfoPanel.Models;
using InfoPanel.Utils;

namespace InfoPanel.Services
{
    /// <summary>Holds the profile that will receive the next "capture foreground app" (hotkey). Set by ProfilesPage when a profile is selected.</summary>
    public static class ProgramSpecificPanelsCaptureService
    {
        public static Profile? TargetProfile { get; set; }
    }
}
