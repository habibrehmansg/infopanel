using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Contract
{
    //this is the main data object that will be returned from each plugin
    public class PanelData
    {
        public string CollectionName { get; set; } //name of the collection. Added this in case if there are multiple values that need to be sent by the plugin
        public IEnumerable<Entry> EntryList { get; set; } //list of entries
    }

    public class Entry
    {
        public string Name { get; set; } //name of entry (Would be something like "Fan Speed", "CPU Usage", "Water Temp",...
        public string Value { get; set; } //The result value. Now while it only accepts strings, you can use something like double.TryParse() to get the value out        
    }

}
