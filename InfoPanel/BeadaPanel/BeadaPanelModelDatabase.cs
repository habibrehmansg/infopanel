using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel
{
    public static class BeadaPanelModelDatabase
    {
        public static readonly Dictionary<BeadaPanelModel, BeadaPanelModelInfo> Models = new()
        {
            [BeadaPanelModel.Model2] = new BeadaPanelModelInfo { Name = "2", Width = 480, Height = 480, WidthMM = 53, HeightMM = 53 },
            [BeadaPanelModel.Model2W] = new BeadaPanelModelInfo { Name = "2W", Width = 480, Height = 480, WidthMM = 70, HeightMM = 70 },
            [BeadaPanelModel.Model3] = new BeadaPanelModelInfo { Name = "3", Width = 320, Height = 480, WidthMM = 40, HeightMM = 62 },
            [BeadaPanelModel.Model4] = new BeadaPanelModelInfo { Name = "4", Width = 480, Height = 800, WidthMM = 56, HeightMM = 94 },
            [BeadaPanelModel.Model3C] = new BeadaPanelModelInfo { Name = "3C", Width = 480, Height = 320, WidthMM = 62, HeightMM = 40 },
            [BeadaPanelModel.Model4C] = new BeadaPanelModelInfo { Name = "4C", Width = 800, Height = 480, WidthMM = 94, HeightMM = 56 },
            [BeadaPanelModel.Model5C] = new BeadaPanelModelInfo { Name = "5C", Width = 800, Height = 480, WidthMM = 108, HeightMM = 65 },
            [BeadaPanelModel.Model5] = new BeadaPanelModelInfo { Name = "5", Width = 800, Height = 480, WidthMM = 108, HeightMM = 65 },
            [BeadaPanelModel.Model5T] = new BeadaPanelModelInfo { Name = "5T", Width = 800, Height = 480, WidthMM = 108, HeightMM = 65 },
            [BeadaPanelModel.Model5S] = new BeadaPanelModelInfo { Name = "5S", Width = 480, Height = 854, WidthMM = 62, HeightMM = 110 },
            [BeadaPanelModel.Model6] = new BeadaPanelModelInfo { Name = "6", Width = 480, Height = 1280, WidthMM = 60, HeightMM = 161 },
            [BeadaPanelModel.Model6C] = new BeadaPanelModelInfo { Name = "6C", Width = 1280, Height = 480, WidthMM = 161, HeightMM = 60 },
            [BeadaPanelModel.Model6S] = new BeadaPanelModelInfo { Name = "6S", Width = 1280, Height = 480, WidthMM = 161, HeightMM = 60 },
            [BeadaPanelModel.Model7C] = new BeadaPanelModelInfo { Name = "7C", Width = 800, Height = 480, WidthMM = 62, HeightMM = 110 },
            [BeadaPanelModel.Model7S] = new BeadaPanelModelInfo { Name = "7S", Width = 1280, Height = 400, WidthMM = 190, HeightMM = 59 },
            [BeadaPanelModel.Model8] = new BeadaPanelModelInfo { Name = "8", Width = 480, Height = 1920, WidthMM = 54, HeightMM = 219 },
            [BeadaPanelModel.ModelY] = new BeadaPanelModelInfo { Name = "Y", Width = 480, Height = 1920, WidthMM = 54, HeightMM = 219 },
            [BeadaPanelModel.Model11] = new BeadaPanelModelInfo { Name = "11", Width = 440, Height = 1920, WidthMM = 58, HeightMM = 253 },
            [BeadaPanelModel.ModelX] = new BeadaPanelModelInfo { Name = "X", Width = 440, Height = 1920, WidthMM = 58, HeightMM = 253 },
            [BeadaPanelModel.Model9] = new BeadaPanelModelInfo { Name = "9", Width = 462, Height = 1920, WidthMM = 55, HeightMM = 226 },
            [BeadaPanelModel.ModelZ] = new BeadaPanelModelInfo { Name = "Z", Width = 462, Height = 1920, WidthMM = 55, HeightMM = 226 }
        };
    }
}
