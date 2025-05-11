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
            [BeadaPanelModel.Model2] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model2, Name = "2", Width = 480, Height = 480, WidthMM = 53, HeightMM = 53 },
            [BeadaPanelModel.Model2W] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model2W, Name = "2W", Width = 480, Height = 480, WidthMM = 70, HeightMM = 70 },
            [BeadaPanelModel.Model3] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model3, Name = "3", Width = 320, Height = 480, WidthMM = 40, HeightMM = 62 },
            [BeadaPanelModel.Model4] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model4, Name = "4", Width = 480, Height = 800, WidthMM = 56, HeightMM = 94 },
            [BeadaPanelModel.Model3C] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model3C, Name = "3C", Width = 480, Height = 320, WidthMM = 62, HeightMM = 40 },
            [BeadaPanelModel.Model4C] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model4C, Name = "4C", Width = 800, Height = 480, WidthMM = 94, HeightMM = 56 },
            [BeadaPanelModel.Model5] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model5, Name = "5", Width = 800, Height = 480, WidthMM = 108, HeightMM = 65 },
            [BeadaPanelModel.Model5T] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model5T, Name = "5T", Width = 800, Height = 480, WidthMM = 108, HeightMM = 65 },
            [BeadaPanelModel.Model5S] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model5S, Name = "5S", Width = 854, Height = 480, WidthMM = 110, HeightMM = 62 },
            [BeadaPanelModel.Model6] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model6, Name = "6", Width = 480, Height = 1280, WidthMM = 60, HeightMM = 161 },
            [BeadaPanelModel.Model6C] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model6C, Name = "6C", Width = 1280, Height = 480, WidthMM = 161, HeightMM = 60 },
            [BeadaPanelModel.Model6S] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model6S, Name = "6S", Width = 1280, Height = 480, WidthMM = 161, HeightMM = 60 },
            [BeadaPanelModel.Model7C] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model7C, Name = "7C", Width = 800, Height = 480, WidthMM = 62, HeightMM = 110 },
            [BeadaPanelModel.Model7S] = new BeadaPanelModelInfo { Id = BeadaPanelModel.Model7S, Name = "7S", Width = 1280, Height = 400, WidthMM = 190, HeightMM = 59 }
        };

        public static BeadaPanelModelInfo? GetInfo(BeadaPanelModel model) =>
            Models.TryGetValue(model, out var info) ? info : null;
    }
}
