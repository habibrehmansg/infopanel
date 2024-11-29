using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Models
{
    internal interface ISensorItem
    {
        SensorType SensorType { get; set; }
        UInt32 Id { get; set; }
        UInt32 Instance { get; set; }
        UInt32 EntryId { get; set; }

        SensorReading? GetValue();
    }
}
