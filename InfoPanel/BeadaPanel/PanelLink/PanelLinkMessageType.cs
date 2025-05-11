using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel.PanelLink
{
    public enum PanelLinkMessageType : byte
    {
        LegacyCommand1 = 1,
        LegacyCommand2 = 2,
        ResetDisplay = 3,
        ClearScreen = 4,
        StartMediaStream = 5,
        EndMediaStream = 6
    }
}
