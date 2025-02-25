using InfoPanel.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.TestPlugin
{
    internal class TestPlugin : BasePlugin
    {
        public TestPlugin(string id, string name, string description) : base(id, name, description)
        {
        }

        public override string? ConfigFilePath => null;

        public override TimeSpan UpdateInterval => TimeSpan.MaxValue;

        public override void Close()
        {
            
        }

        public override void Initialize()
        {
            
        }

        public override void Load(List<IPluginContainer> containers)
        {
            
        }

        public override void Update()
        {
            
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
