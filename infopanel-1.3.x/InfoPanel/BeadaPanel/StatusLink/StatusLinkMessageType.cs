using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel.StatusLink
{
    public enum StatusLinkMessageType : byte
    {
        GetPanelInfo = 1,
        PanelLinkReset = 2,
        SetBacklight = 3,
        PushStorage = 4,
        GetTime = 5,
        SetTime = 6
    }
}
