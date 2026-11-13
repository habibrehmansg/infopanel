using System.Data;

namespace InfoPanel.Plugins
{
    public interface IPluginTable: IPluginData
    {
        DataTable Value { get; set; }
        string DefaultFormat { get; }
    }
}
