using InfoPanel.Plugins;

namespace InfoPanel.TestPlug
{
    public class TestPlugin : BasePlugin
    {
        

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(5);

        public TestPlugin() : base("test-plugin", "Testing Plugin", "Plugin made for testing")
        {
        }

        public override void Initialize()
        {
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Test container");
            
        }

        public override void Close()
        {
            
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            
        }

        
    }
}
