using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel
{
    public class BeadaPanelModelInfo
    {
        public BeadaPanelModel Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public int WidthMM { get; init; }
        public int HeightMM { get; init; }

        public override string ToString() => $"{Name} ({Width}x{Height})";
    }
}
