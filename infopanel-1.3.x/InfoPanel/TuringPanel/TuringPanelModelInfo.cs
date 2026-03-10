using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.TuringPanel
{
    public class TuringPanelModelInfo
    {
        public TuringPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }

        public bool IsUsbDevice { get; init; } = false;

        public override string ToString() => $"{Name} ({Width}x{Height}) - VID: {VendorId:X4}, PID: {ProductId:X4}";

    }
}
